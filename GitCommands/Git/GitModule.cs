using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Security.Permissions;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GitCommands.Config;
using GitCommands.Git;
using GitCommands.Git.Extensions;
using GitCommands.Settings;
using GitCommands.Utils;
using GitUI;
using GitUIPluginInterfaces;
using JetBrains.Annotations;
using Microsoft.VisualStudio.Threading;
using PatchApply;
using SmartFormat;

namespace GitCommands
{
    public class GitModuleEventArgs : EventArgs
    {
        public GitModuleEventArgs(GitModule gitModule)
        {
            GitModule = gitModule;
        }

        public GitModule GitModule { get; private set; }
    }

    public enum SubmoduleStatus
    {
        Unknown,
        NewSubmodule,
        FastForward,
        Rewind,
        NewerTime,
        OlderTime,
        SameTime
    }

    public enum ForcePushOptions
    {
        DoNotForce,
        Force,
        ForceWithLease,
    }

    public struct ConflictedFileData
    {
        public ConflictedFileData(string hash, string filename)
        {
            Hash = hash;
            Filename = filename;
        }
        public string Hash;
        public string Filename;
    }

    [DebuggerDisplay("{Filename}")]
    public struct ConflictData
    {
        public ConflictData(ConflictedFileData _base, ConflictedFileData _local,
            ConflictedFileData _remote)
        {
            Base = _base;
            Local = _local;
            Remote = _remote;
        }
        public ConflictedFileData Base;
        public ConflictedFileData Local;
        public ConflictedFileData Remote;

        public string Filename
        {
            get { return Local.Filename ?? Base.Filename ?? Remote.Filename; }
        }
    }

    /// <summary>Provides manipulation with git module.
    /// <remarks>Several instances may be created for submodules.</remarks></summary>
    [DebuggerDisplay("GitModule ( {_workingDir} )")]
    public sealed class GitModule : IGitModule
    {
        private static readonly Regex DefaultHeadPattern = new Regex("refs/remotes/[^/]+/HEAD", RegexOptions.Compiled);
        private static readonly Regex AnsiCodePattern = new Regex(@"\u001B[\u0040-\u005F].*?[\u0040-\u007E]", RegexOptions.Compiled);
        private static readonly Regex CpEncodingPattern = new Regex("cp\\d+", RegexOptions.Compiled);
        private readonly object _lock = new object();
        private readonly IIndexLockManager _indexLockManager;
        private readonly ICommitDataManager _commitDataManager;
        private static readonly IGitDirectoryResolver GitDirectoryResolverInstance = new GitDirectoryResolver();
        private readonly IGitTreeParser _gitTreeParser = new GitTreeParser();
        private readonly IRevisionDiffProvider _revisionDiffProvider = new RevisionDiffProvider();


        public const string NoNewLineAtTheEnd = "\\ No newline at end of file";
        private const string DiffCommandWithStandardArgs = " -c diff.submodule=short diff --no-color ";

        public GitModule(string workingdir)
        {
            _superprojectInit = false;
            _workingDir = (workingdir ?? "").EnsureTrailingPathSeparator();
            WorkingDirGitDir = GitDirectoryResolverInstance.Resolve(_workingDir);
            _indexLockManager = new IndexLockManager(this);
            _commitDataManager = new CommitDataManager(() => this);
        }

        #region IGitCommands

        private readonly string _workingDir;

        /// <summary>
        /// Gets the directory which contains the git repository.
        /// </summary>
        [NotNull]
        public string WorkingDir => _workingDir;

        /// <summary>
        /// Gets the location of .git directory for the current working folder.
        /// </summary>
        [NotNull]
        public string WorkingDirGitDir { get; private set; }

        /// <summary>Gets the path to the git application executable.</summary>
        public string GitCommand
        {
            get
            {
                return AppSettings.GitCommand;
            }
        }

        public Version AppVersion
        {
            get
            {
                return AppSettings.AppVersion;
            }
        }

        public string GravatarCacheDir
        {
            get
            {
                return AppSettings.GravatarCachePath;
            }
        }

        #endregion

        private bool _superprojectInit;
        private GitModule _superprojectModule;
        private string _submoduleName;
        private string _submodulePath;

        public string SubmoduleName
        {
            get
            {
                InitSuperproject();
                return _submoduleName;
            }
        }

        public string SubmodulePath
        {
            get
            {
                InitSuperproject();
                return _submodulePath;
            }
        }

        public GitModule SuperprojectModule
        {
            get
            {
                InitSuperproject();
                return _superprojectModule;
            }
        }

        private void InitSuperproject()
        {
            if (!_superprojectInit)
            {
                string superprojectDir = FindGitSuperprojectPath(out _submoduleName, out _submodulePath);
                _superprojectModule = superprojectDir == null ? null : new GitModule(superprojectDir);
                _superprojectInit = true;
            }
        }

        public GitModule FindTopProjectModule()
        {
            GitModule module = SuperprojectModule;
            if (module == null)
                return null;
            do
            {
                if (module.SuperprojectModule == null)
                    return module;
                module = module.SuperprojectModule;
            } while (module != null);
            return module;
        }

        private RepoDistSettings _effectiveSettings;
        public RepoDistSettings EffectiveSettings
        {
            get
            {
                lock (_lock)
                {
                    if (_effectiveSettings == null)
                        _effectiveSettings = RepoDistSettings.CreateEffective(this);
                }

                return _effectiveSettings;
            }
        }

        public ISettingsSource GetEffectiveSettings()
        {
            return EffectiveSettings;
        }

        private RepoDistSettings _distributedSettings;
        public RepoDistSettings DistributedSettings
        {
            get
            {
                lock (_lock)
                {
                    if (_distributedSettings == null)
                        _distributedSettings = new RepoDistSettings(null, EffectiveSettings.LowerPriority.SettingsCache);
                }

                return _distributedSettings;
            }
        }

        private RepoDistSettings _localSettings;
        public RepoDistSettings LocalSettings
        {
            get
            {
                lock (_lock)
                {
                    if (_localSettings == null)
                        _localSettings = new RepoDistSettings(null, EffectiveSettings.SettingsCache);
                }

                return _localSettings;
            }
        }

        private ConfigFileSettings _effectiveConfigFile;
        public ConfigFileSettings EffectiveConfigFile
        {
            get
            {
                lock (_lock)
                {
                    if (_effectiveConfigFile == null)
                        _effectiveConfigFile = ConfigFileSettings.CreateEffective(this);
                }

                return _effectiveConfigFile;
            }
        }

        public ConfigFileSettings LocalConfigFile
        {
            get { return new ConfigFileSettings(null, EffectiveConfigFile.SettingsCache); }
        }

        IConfigFileSettings IGitModule.LocalConfigFile
        {
            get { return LocalConfigFile; }
        }

        //encoding for files paths
        private static Encoding _systemEncoding;
        public static Encoding SystemEncoding
        {
            get
            {
                if (_systemEncoding == null)
                {
                    //check whether GitExtensions works with standard msysgit or msysgit-unicode

                    // invoke a git command that returns an invalid argument in its response, and
                    // check if a unicode-only character is reported back. If so assume msysgit-unicode

                    // git config --get with a malformed key (no section) returns:
                    // "error: key does not contain a section: <key>"
                    const string controlStr = "ą"; // "a caudata"
                    string arguments = string.Format("config --get {0}", controlStr);

                    String s = ThreadHelper.JoinableTaskFactory.Run(() => new GitModule("").RunGitCmdAsync(arguments, Encoding.UTF8));
                    if (s != null && s.IndexOf(controlStr) != -1)
                        _systemEncoding = new UTF8Encoding(false);
                    else
                        _systemEncoding = Encoding.Default;

                    Debug.WriteLine("System encoding: " + _systemEncoding.EncodingName);
                }

                return _systemEncoding;
            }
        }

        //Encoding that let us read all bytes without replacing any char
        //It is using to read output of commands, which may consist of:
        //1) commit header (message, author, ...) encoded in CommitEncoding, recoded to LogOutputEncoding or not dependent of
        //   pretty parameter (pretty=raw - recoded, pretty=format - not recoded)
        //2) file content encoded in its original encoding
        //3) file path (file name is encoded in system default encoding),
        //   when core.quotepath is on, every non ASCII character is escaped
        //   with \ followed by its code as a three digit octal number
        //4) branch, tag name, errors, warnings, hints encoded in system default encoding
        public static readonly Encoding LosslessEncoding = Encoding.GetEncoding("ISO-8859-1");//is any better?

        public Encoding FilesEncoding
        {
            get
            {
                Encoding result = EffectiveConfigFile.FilesEncoding;
                if (result == null)
                    result = new UTF8Encoding(false);
                return result;
            }
        }

        public Encoding CommitEncoding
        {
            get
            {
                Encoding result = EffectiveConfigFile.CommitEncoding;
                if (result == null)
                    result = new UTF8Encoding(false);
                return result;
            }
        }

        /// <summary>
        /// Encoding for commit header (message, notes, author, committer, emails)
        /// </summary>
        public Encoding LogOutputEncoding
        {
            get
            {
                Encoding result = EffectiveConfigFile.LogOutputEncoding;
                if (result == null)
                    result = CommitEncoding;
                return result;
            }
        }

        /// <summary>"(no branch)"</summary>
        public static readonly string DetachedBranch = "(no branch)";

        private static readonly string[] DetachedPrefixes = { "(no branch", "(detached from ", "(HEAD detached at " };

        public AppSettings.PullAction LastPullAction
        {
            get { return AppSettings.GetEnum("LastPullAction_" + WorkingDir, AppSettings.PullAction.None); }
            set { AppSettings.SetEnum("LastPullAction_" + WorkingDir, value); }
        }

        public void LastPullActionToFormPullAction()
        {
            if (LastPullAction == AppSettings.PullAction.FetchAll)
                AppSettings.FormPullAction = AppSettings.PullAction.Fetch;
            else if (LastPullAction != AppSettings.PullAction.None)
                AppSettings.FormPullAction = LastPullAction;
        }

        /// <summary>Indicates whether the <see cref="WorkingDir"/> contains a git repository.</summary>
        public bool IsValidGitWorkingDir()
        {
            return IsValidGitWorkingDir(_workingDir);
        }

        /// <summary>Indicates whether the specified directory contains a git repository.</summary>
        public static bool IsValidGitWorkingDir(string dir)
        {
            if (string.IsNullOrEmpty(dir))
                return false;

            string dirPath = dir.EnsureTrailingPathSeparator();
            string path = dirPath + ".git";

            if (Directory.Exists(path) || File.Exists(path))
                return true;

            return Directory.Exists(dirPath + "info") &&
                   Directory.Exists(dirPath + "objects") &&
                   Directory.Exists(dirPath + "refs");
        }

        /// <summary>
        /// Asks git to resolve the given relativePath
        /// git special folders are located in different directories depending on the kind of repo: submodule, worktree, main
        /// See https://git-scm.com/docs/git-rev-parse#git-rev-parse---git-pathltpathgt
        /// </summary>
        /// <param name="relativePath">A path relative to the .git directory</param>
        /// <returns></returns>
        public async Task<string> ResolveGitInternalPathAsync(string relativePath)
        {
            string gitPath = await RunGitCmdAsync("rev-parse --git-path " + relativePath.Quote()).ConfigureAwait(false);
            string systemPath = PathUtil.ToNativePath(gitPath.Trim());
            if (systemPath.StartsWith(".git\\"))
            {
                systemPath = Path.Combine(GetGitDirectory(), systemPath.Substring(".git\\".Length));
            }
            return systemPath;
        }

        private string _GitCommonDirectory;
        /// <summary>
        /// Returns git common directory
        /// https://git-scm.com/docs/git-rev-parse#git-rev-parse---git-common-dir
        /// </summary>
        public string GitCommonDirectory
        {
            get
            {
                if (_GitCommonDirectory == null)
                {
                    var commDir = ThreadHelper.JoinableTaskFactory.Run(() => RunGitCmdResultAsync("rev-parse --git-common-dir"));
                    _GitCommonDirectory = PathUtil.ToNativePath(commDir.StdOutput.Trim());
                    if (!commDir.ExitedSuccessfully || _GitCommonDirectory == ".git" || !Directory.Exists(_GitCommonDirectory))
                    {
                        _GitCommonDirectory = GetGitDirectory();
                    }
                }

                return _GitCommonDirectory;
            }
        }

        /// <summary>Gets the ".git" directory path.</summary>
        private string GetGitDirectory()
        {
            return GetGitDirectory(_workingDir);
        }

        public static string GetGitDirectory(string repositoryPath)
        {
            return GitDirectoryResolverInstance.Resolve(repositoryPath);
        }

        public bool IsBareRepository()
        {
            return WorkingDir == GetGitDirectory();
        }

        public static bool IsBareRepository(string repositoryPath)
        {
            return repositoryPath == GetGitDirectory(repositoryPath);
        }

        public async Task<bool> IsSubmoduleAsync(string submodulePath)
        {
            var result = await RunGitCmdResultAsync("submodule status " + submodulePath).ConfigureAwait(false);

            if (result.ExitCode == 0
                // submodule removed
                || result.StdError.StartsWith("No submodule mapping found in .gitmodules for path"))
                return true;

            return false;
        }

        public bool HasSubmodules()
        {
            return GetSubmodulesLocalPaths(recursive: false).Any();
        }

        /// <summary>
        /// This is a faster function to get the names of all submodules then the
        /// GetSubmodules() function. The command @git submodule is very slow.
        /// </summary>
        public IList<string> GetSubmodulesLocalPaths(bool recursive = true)
        {
            var configFile = GetSubmoduleConfigFile();
            var submodules = configFile.ConfigSections.Select(configSection => configSection.GetValue("path").Trim()).ToList();
            if (recursive)
            {
                for (int i = 0; i < submodules.Count; i++)
                {
                    var submodule = GetSubmodule(submodules[i]);
                    var submoduleConfigFile = submodule.GetSubmoduleConfigFile();
                    var subsubmodules = submoduleConfigFile.ConfigSections.Select(configSection => configSection.GetValue("path").Trim()).ToList();
                    for (int j = 0; j < subsubmodules.Count; j++)
                        subsubmodules[j] = submodules[i] + '/' + subsubmodules[j];
                    submodules.InsertRange(i + 1, subsubmodules);
                    i += subsubmodules.Count;
                }
            }
            return submodules;
        }

        public static string FindGitWorkingDir(string startDir)
        {
            if (string.IsNullOrEmpty(startDir))
                return "";

            var dir = startDir.Trim();

            do
            {
                if (IsValidGitWorkingDir(dir))
                    return dir.EnsureTrailingPathSeparator();

                dir = PathUtil.GetDirectoryName(dir);
            }
            while (!string.IsNullOrEmpty(dir));
            return startDir;
        }

        private static Process StartProccess(string fileName, string arguments, string workingDir, bool showConsole)
        {
            GitCommandHelpers.SetEnvironmentVariable();

            string quotedCmd = fileName;
            if (quotedCmd.IndexOf(' ') != -1)
                quotedCmd = quotedCmd.Quote();

            var executionStartTimestamp = DateTime.Now;

            var startInfo = new ProcessStartInfo
                                {
                                    FileName = fileName,
                                    Arguments = arguments,
                                    WorkingDirectory = workingDir
                                };
            if (!showConsole)
            {
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
            }

            var startProcess = Process.Start(startInfo);

            startProcess.Exited += (sender, args) =>
            {
                var executionEndTimestamp = DateTime.Now;
                AppSettings.GitLog.Log(quotedCmd + " " + arguments, executionStartTimestamp, executionEndTimestamp);
            };

            return startProcess;
        }

        public string StripAnsiCodes(string input)
        {
            // The following does return the original string if no ansi codes are found
            return AnsiCodePattern.Replace(input, "");
        }

        /// <summary>
        /// Run command, console window is visible
        /// </summary>
        public Process RunExternalCmdDetachedShowConsole(string cmd, string arguments)
        {
            try
            {
                return StartProccess(cmd, arguments, _workingDir, showConsole: true);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.Message);
            }

            return null;
        }

        /// <summary>
        /// Run command, console window is visible, wait for exit
        /// </summary>
        public void RunExternalCmdShowConsole(string cmd, string arguments)
        {
            try
            {
                using (var process = StartProccess(cmd, arguments, _workingDir, showConsole: true))
                    process.WaitForExit();
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.Message);
            }
        }

        /// <summary>
        /// Run command, console window is hidden
        /// </summary>
        public static Process RunExternalCmdDetached(string fileName, string arguments, string workingDir)
        {
            try
            {
                return StartProccess(fileName, arguments, workingDir, showConsole: false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.Message);
            }

            return null;
        }

        /// <summary>
        /// Run command, console window is hidden
        /// </summary>
        public Process RunExternalCmdDetached(string cmd, string arguments)
        {
            return RunExternalCmdDetached(cmd, arguments, _workingDir);
        }

        /// <summary>
        /// Run git command, console window is hidden, redirect output
        /// </summary>
        public Process RunGitCmdDetached(string arguments, Encoding encoding = null)
        {
            if (encoding == null)
                encoding = SystemEncoding;

            return GitCommandHelpers.StartProcess(AppSettings.GitCommand, arguments, _workingDir, encoding);
        }

        /// <summary>
        /// Run command, cache results, console window is hidden, wait for exit, redirect output
        /// </summary>
        [PermissionSet(SecurityAction.Demand, Name = "FullTrust")]
        public async Task<string> RunCacheableCmdAsync(string cmd, string arguments = "", Encoding encoding = null)
        {
            if (encoding == null)
                encoding = SystemEncoding;

            byte[] cmdout, cmderr;
            if (GitCommandCache.TryGet(arguments, out cmdout, out cmderr))
                return StripAnsiCodes(EncodingHelper.DecodeString(cmdout, cmderr, ref encoding));

            (_, cmdout, cmderr) = await GitCommandHelpers.RunCmdByteAsync(cmd, arguments, _workingDir, null).ConfigureAwait(false);

            GitCommandCache.Add(arguments, cmdout, cmderr);

            return StripAnsiCodes(EncodingHelper.DecodeString(cmdout, cmderr, ref encoding));
        }

        /// <summary>
        /// Run command, console window is hidden, wait for exit, redirect output
        /// </summary>
        [PermissionSet(SecurityAction.Demand, Name = "FullTrust")]
        public async Task<CmdResult> RunCmdResultAsync(string cmd, string arguments, Encoding encoding = null, byte[] stdInput = null)
        {
            var (exitCode, output, error) = await GitCommandHelpers.RunCmdByteAsync(cmd, arguments, _workingDir, stdInput);
            if (encoding == null)
                encoding = SystemEncoding;
            return new CmdResult
            {
                StdOutput = output == null ? string.Empty : StripAnsiCodes(encoding.GetString(output)),
                StdError = error == null ? string.Empty : StripAnsiCodes(encoding.GetString(error)),
                ExitCode = exitCode
            };
        }

        /// <summary>
        /// Run command, console window is hidden, wait for exit, redirect output
        /// </summary>
        [PermissionSet(SecurityAction.Demand, Name = "FullTrust")]
        public async Task<string> RunCmdAsync(string cmd, string arguments, Encoding encoding = null, byte[] stdInput = null)
        {
            return (await RunCmdResultAsync(cmd, arguments, encoding, stdInput).ConfigureAwait(false)).GetString();
        }

        /// <summary>
        /// Run git command, console window is hidden, wait for exit, redirect output
        /// </summary>
        public async Task<string> RunGitCmdAsync(string arguments, Encoding encoding = null, byte[] stdInput = null)
        {
            return await RunCmdAsync(AppSettings.GitCommand, arguments, encoding, stdInput).ConfigureAwait(false);
        }

        /// <summary>
        /// Run git command, console window is hidden, wait for exit, redirect output
        /// </summary>
        public async Task<CmdResult> RunGitCmdResultAsync(string arguments, Encoding encoding = null, byte[] stdInput = null)
        {
            return await RunCmdResultAsync(AppSettings.GitCommand, arguments, encoding, stdInput).ConfigureAwait(false);
        }

        /// <summary>
        /// Run command, console window is hidden, wait for exit, redirect output
        /// </summary>
        [PermissionSet(SecurityAction.Demand, Name = "FullTrust")]
        private IEnumerable<string> ReadCmdOutputLines(string cmd, string arguments, string stdInput)
        {
            return GitCommandHelpers.ReadCmdOutputLines(cmd, arguments, _workingDir, stdInput);
        }

        /// <summary>
        /// Run git command, console window is hidden, wait for exit, redirect output
        /// </summary>
        public IEnumerable<string> ReadGitOutputLines(string arguments)
        {
            return ReadCmdOutputLines(AppSettings.GitCommand, arguments, null);
        }

        /// <summary>
        /// Run batch file, console window is hidden, wait for exit, redirect output
        /// </summary>
        public async Task<string> RunBatchFileAsync(string batchFile)
        {
            string tempFileName = Path.ChangeExtension(Path.GetTempFileName(), ".cmd");
            using (var writer = new StreamWriter(tempFileName))
            {
                await writer.WriteLineAsync("@prompt $G").ConfigureAwait(false);
                await writer.WriteAsync(batchFile).ConfigureAwait(false);
            }
            string result = await RunCmdAsync("cmd.exe", "/C \"" + tempFileName + "\"").ConfigureAwait(false);
            File.Delete(tempFileName);
            return result;
        }

        public async Task EditNotesAsync(string revision)
        {
            string editor = GetEffectiveSetting("core.editor").ToLower();
            if (editor.Contains("gitextensions") || editor.Contains("notepad"))
            {
                await RunGitCmdAsync("notes edit " + revision).ConfigureAwait(false);
            }
            else
            {
                RunExternalCmdShowConsole(AppSettings.GitCommand, "notes edit " + revision);
            }
        }

        public async Task<bool> InTheMiddleOfConflictedMergeAsync()
        {
            return !string.IsNullOrEmpty(await RunGitCmdAsync("ls-files -z --unmerged").ConfigureAwait(false));
        }

        public async Task<bool> HandleConflictSelectSideAsync(string fileName, string side)
        {
            Directory.SetCurrentDirectory(_workingDir);
            fileName = fileName.ToPosixPath();

            side = GetSide(side);

            string result = await RunGitCmdAsync(String.Format("checkout-index -f --stage={0} -- \"{1}\"", side, fileName)).ConfigureAwait(false);
            if (!result.IsNullOrEmpty())
            {
                return false;
            }

            result = await RunGitCmdAsync(String.Format("add -- \"{0}\"", fileName)).ConfigureAwait(false);
            return result.IsNullOrEmpty();
        }

        public async Task<bool> HandleConflictsSaveSideAsync(string fileName, string saveAsFileName, string side)
        {
            Directory.SetCurrentDirectory(_workingDir);
            fileName = fileName.ToPosixPath();

            side = GetSide(side);

            var result = await RunGitCmdAsync(String.Format("checkout-index --stage={0} --temp -- \"{1}\"", side, fileName)).ConfigureAwait(false);
            if (result.IsNullOrEmpty())
            {
                return false;
            }

            if (!result.StartsWith(".merge_file_"))
            {
                return false;
            }

            // Parse temporary file name from command line result
            var splitResult = result.Split(new[] { "\t", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
            if (splitResult.Length != 2)
            {
                return false;
            }

            var temporaryFileName = splitResult[0].Trim();

            if (!File.Exists(temporaryFileName))
            {
                return false;
            }

            var retValue = false;
            try
            {
                if (File.Exists(saveAsFileName))
                {
                    File.Delete(saveAsFileName);
                }
                File.Move(temporaryFileName, saveAsFileName);
                retValue = true;
            }
            catch
            {
            }
            finally
            {
                if (File.Exists(temporaryFileName))
                {
                    File.Delete(temporaryFileName);
                }
            }

            return retValue;
        }

        public async Task SaveBlobAsAsync(string saveAs, string blob)
        {
            using (var ms = (MemoryStream)GetFileStream(blob)) //Ugly, has implementation info.
            {
                byte[] buf = ms.ToArray();
                if (EffectiveConfigFile.core.autocrlf.ValueOrDefault == AutoCRLFType.@true)
                {
                    if (!await FileHelper.IsBinaryFileAsync(this, saveAs).ConfigureAwait(false) && !FileHelper.IsBinaryFileAccordingToContent(buf))
                    {
                        buf = GitConvert.ConvertCrLfToWorktree(buf);
                    }
                }

                using (FileStream fileOut = File.Create(saveAs))
                {
                    await fileOut.WriteAsync(buf, 0, buf.Length).ConfigureAwait(false);
                }
            }
        }

        private static string GetSide(string side)
        {
            if (side.Equals("REMOTE", StringComparison.CurrentCultureIgnoreCase))
                side = "3";
            if (side.Equals("LOCAL", StringComparison.CurrentCultureIgnoreCase))
                side = "2";
            if (side.Equals("BASE", StringComparison.CurrentCultureIgnoreCase))
                side = "1";
            return side;
        }

        public async Task<string[]> CheckoutConflictedFilesAsync(ConflictData unmergedData)
        {
            Directory.SetCurrentDirectory(_workingDir);

            var filename = unmergedData.Filename;

            string[] fileNames =
                {
                    filename + ".BASE",
                    filename + ".LOCAL",
                    filename + ".REMOTE"
                };

            var unmerged = new[] { unmergedData.Base.Filename, unmergedData.Local.Filename, unmergedData.Remote.Filename };

            for (int i = 0; i < unmerged.Length; i++)
            {
                if (unmerged[i] == null)
                    continue;
                var tempFile =
                    await RunGitCmdAsync("checkout-index --temp --stage=" + (i + 1) + " -- \"" + filename + "\"").ConfigureAwait(false);
                tempFile = tempFile.Split('\t')[0];
                tempFile = Path.Combine(_workingDir, tempFile);

                var newFileName = Path.Combine(_workingDir, fileNames[i]);
                try
                {
                    fileNames[i] = newFileName;
                    var index = 1;
                    while (File.Exists(fileNames[i]) && index < 50)
                    {
                        fileNames[i] = newFileName + index;
                        index++;
                    }
                    File.Move(tempFile, fileNames[i]);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex);
                }
            }

            if (!File.Exists(fileNames[0])) fileNames[0] = null;
            if (!File.Exists(fileNames[1])) fileNames[1] = null;
            if (!File.Exists(fileNames[2])) fileNames[2] = null;

            return fileNames;
        }

        public async Task<ConflictData> GetConflictAsync(string filename)
        {
            return (await GetConflictsAsync(filename).ConfigureAwait(false)).SingleOrDefault();
        }

        public async Task<List<ConflictData>> GetConflictsAsync(string filename = "")
        {
            filename = filename.ToPosixPath();

            var list = new List<ConflictData>();

            var unmerged = (await RunGitCmdAsync("ls-files -z --unmerged " + filename.QuoteNE()).ConfigureAwait(false)).Split(new[] { '\0', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var item = new ConflictedFileData[3];

            string prevItemName = null;

            foreach (var line in unmerged)
            {
                int findSecondWhitespace = line.IndexOfAny(new[] { ' ', '\t' });
                string fileStage = findSecondWhitespace >= 0 ? line.Substring(findSecondWhitespace).Trim() : "";

                findSecondWhitespace = fileStage.IndexOfAny(new[] { ' ', '\t' });

                string hash = findSecondWhitespace >= 0 ? fileStage.Substring(0, findSecondWhitespace).Trim() : "";
                fileStage = findSecondWhitespace >= 0 ? fileStage.Substring(findSecondWhitespace).Trim() : "";

                int stage;
                if (fileStage.Length > 2 && Int32.TryParse(fileStage[0].ToString(), out stage) && stage >= 1 && stage <= 3)
                {
                    var itemName = fileStage.Substring(2);
                    if (prevItemName != itemName && prevItemName != null)
                    {
                        list.Add(new ConflictData(item[0], item[1], item[2]));
                        item = new ConflictedFileData[3];
                    }
                    item[stage - 1] = new ConflictedFileData(hash, itemName);
                    prevItemName = itemName;
                }
            }
            if (prevItemName != null)
                list.Add(new ConflictData(item[0], item[1], item[2]));

            return list;
        }

        public async Task<IList<string>> GetSortedRefsAsync()
        {
            string command = "for-each-ref --sort=-committerdate --sort=-taggerdate --format=\"%(refname)\" refs/";

            var tree = await RunGitCmdAsync(command, SystemEncoding).ConfigureAwait(false);

            return tree.Split();
        }

        public async Task<Dictionary<IGitRef, IGitItem>> GetSubmoduleItemsForEachRefAsync(string filename, Func<IGitRef, bool> showRemoteRef)
        {
            string command = GetSortedRefsCommand();

            if (command == null)
                return new Dictionary<IGitRef, IGitItem>();

            filename = filename.ToPosixPath();

            var tree = await RunGitCmdAsync(command, SystemEncoding).ConfigureAwait(false);

            var refs = await GetTreeRefsAsync(tree).ConfigureAwait(false);

            var dictionary = new Dictionary<IGitRef, IGitItem>();
            foreach (var r in refs.Where(showRemoteRef))
            {
                dictionary.Add(r, await GetSubmoduleCommitHashAsync(filename, r.Name));
            }

            return dictionary;
        }

        private string GetSortedRefsCommand()
        {
            if (AppSettings.ShowSuperprojectRemoteBranches)
                return "for-each-ref --sort=-committerdate --format=\"%(objectname) %(refname)\" refs/";

            if (AppSettings.ShowSuperprojectBranches || AppSettings.ShowSuperprojectTags)
                return "for-each-ref --sort=-committerdate --format=\"%(objectname) %(refname)\""
                    + (AppSettings.ShowSuperprojectBranches ? " refs/heads/" : null)
                    + (AppSettings.ShowSuperprojectTags ? " refs/tags/" : null);

            return null;
        }

        private async Task<IGitItem> GetSubmoduleCommitHashAsync(string filename, string refName)
        {
            string str = await RunGitCmdAsync("ls-tree " + refName + " \"" + filename + "\"").ConfigureAwait(false);
            return _gitTreeParser.ParseSingle(str);
        }

        public async Task<int?> GetCommitCountAsync(string parentHash, string childHash)
        {
            string result = await RunGitCmdAsync("rev-list " + parentHash + " ^" + childHash + " --count").ConfigureAwait(false);
            int commitCount;
            if (int.TryParse(result, out commitCount))
                return commitCount;
            return null;
        }

        public async Task<string> GetCommitCountStringAsync(string from, string to)
        {
            int? removed = await GetCommitCountAsync(from, to).ConfigureAwait(false);
            int? added = await GetCommitCountAsync(to, from).ConfigureAwait(false);

            if (removed == null || added == null)
                return "";
            if (removed == 0 && added == 0)
                return "=";

            return
                (removed > 0 ? ("-" + removed) : "") +
                (added > 0 ? ("+" + added) : "");
        }

        public string GetMergeMessage()
        {
            var file = GetGitDirectory() + "MERGE_MSG";

            return
                File.Exists(file)
                    ? File.ReadAllText(file)
                    : "";
        }

        public void RunGitK()
        {
            if (EnvUtils.RunningOnUnix())
            {
                RunExternalCmdDetachedShowConsole("gitk", "");
            }
            else
            {
                RunExternalCmdDetached("cmd.exe", "/c \"\"" + AppSettings.GitCommand.Replace("git.cmd", "gitk.cmd")
                                                              .Replace("bin\\git.exe", "cmd\\gitk.cmd")
                                                              .Replace("bin/git.exe", "cmd/gitk.cmd") + "\" --branches --tags --remotes\"");
            }
        }

        public void RunGui()
        {
            if (EnvUtils.RunningOnUnix())
            {
                RunExternalCmdDetachedShowConsole(AppSettings.GitCommand, "gui");
            }
            else
            {
                RunExternalCmdDetached("cmd.exe", "/c \"\"" + AppSettings.GitCommand + "\" gui\"");
            }
        }

        /// <summary>Runs a bash or shell command.</summary>
        public async Task<Process> RunBashAsync(string bashCommand = null)
        {
            if (EnvUtils.RunningOnUnix())
            {
                string[] termEmuCmds =
                {
                    "gnome-terminal",
                    "konsole",
                    "Terminal",
                    "xterm"
                };

                string args = "";
                string cmd = null;
                foreach (var termEmuCmd in termEmuCmds)
                {
                    if (!string.IsNullOrEmpty(await RunCmdAsync("which", termEmuCmd).ConfigureAwait(false)))
                    {
                        cmd = termEmuCmd;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(cmd))
                {
                    cmd = "bash";
                    args = "--login -i";
                }

                return RunExternalCmdDetachedShowConsole(cmd, args);
            }
            else
            {
                string shellPath;
                if (PathUtil.TryFindShellPath("git-bash.exe", out shellPath))
                {
                    return RunExternalCmdDetachedShowConsole(shellPath, string.Empty);
                }

                string args;
                if (string.IsNullOrWhiteSpace(bashCommand))
                {
                    args = "--login -i\"";
                }
                else
                {
                    args = "--login -i -c \"" + bashCommand.Replace("\"", "\\\"") + "\"";
                }
                args = "/c \"\"{0}\" " + args;

                if (PathUtil.TryFindShellPath("bash.exe", out shellPath))
                {
                    return RunExternalCmdDetachedShowConsole("cmd.exe", string.Format(args, shellPath));
                }

                if (PathUtil.TryFindShellPath("sh.exe", out shellPath))
                {
                    return RunExternalCmdDetachedShowConsole("cmd.exe", string.Format(args, shellPath));
                }

                return RunExternalCmdDetachedShowConsole("cmd.exe", @"/K echo git bash command not found! :( Please add a folder containing 'bash.exe' to your PATH...");
            }
        }

        public async Task<string> InitAsync(bool bare, bool shared)
        {
            var result = await RunGitCmdAsync(Smart.Format("init{0: --bare|}{1: --shared=all|}", bare, shared)).ConfigureAwait(false);
            WorkingDirGitDir = GitDirectoryResolverInstance.Resolve(_workingDir);
            return result;
        }

        public async Task<bool> IsMergeAsync(string commit)
        {
            string[] parents = await GetParentsAsync(commit).ConfigureAwait(false);
            return parents.Length > 1;
        }

        private static string ProccessDiffNotes(int startIndex, string[] lines)
        {
            int endIndex = lines.Length - 1;
            if (lines[endIndex] == "Notes:")
                endIndex--;

            var message = new StringBuilder();
            bool bNotesStart = false;
            for (int i = startIndex; i <= endIndex; i++)
            {
                string line = lines[i];
                if (bNotesStart)
                    line = "    " + line;
                message.AppendLine(line);
                if (lines[i] == "Notes:")
                    bNotesStart = true;
            }

            return message.ToString();
        }

        public async Task<GitRevision> GetRevisionAsync(string commit, bool shortFormat = false)
        {
            const string formatString =
                /* Hash           */ "%H%n" +
                /* Tree           */ "%T%n" +
                /* Parents        */ "%P%n" +
                /* Author Name    */ "%aN%n" +
                /* Author EMail   */ "%aE%n" +
                /* Author Date    */ "%at%n" +
                /* Committer Name */ "%cN%n" +
                /* Committer EMail*/ "%cE%n" +
                /* Committer Date */ "%ct%n";
            const string messageFormat = "%e%n%B%nNotes:%n%-N";
            string cmd = "log -n1 --format=format:" + formatString + (shortFormat ? "%e%n%s" : messageFormat) + " " + commit;
            var revInfo = await RunCacheableCmdAsync(AppSettings.GitCommand, cmd, LosslessEncoding).ConfigureAwait(false);
            string[] lines = revInfo.Split('\n');
            var revision = new GitRevision(lines[0])
            {
                TreeGuid = lines[1],
                ParentGuids = lines[2].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
                Author = ReEncodeStringFromLossless(lines[3]),
                AuthorEmail = ReEncodeStringFromLossless(lines[4]),
                Committer = ReEncodeStringFromLossless(lines[6]),
                CommitterEmail = ReEncodeStringFromLossless(lines[7])
            };
            revision.AuthorDate = DateTimeUtils.ParseUnixTime(lines[5]);
            revision.CommitDate = DateTimeUtils.ParseUnixTime(lines[8]);
            revision.MessageEncoding = lines[9];
            if (shortFormat)
            {
                revision.Subject = ReEncodeCommitMessage(lines[10], revision.MessageEncoding);
            }
            else
            {
                string message = ProccessDiffNotes(10, lines);

                //commit message is not reencoded by git when format is given
                revision.Body = ReEncodeCommitMessage(message, revision.MessageEncoding);
                revision.Subject = revision.Body.Substring(0, revision.Body.IndexOfAny(new[] { '\r', '\n' }));
            }

            return revision;
        }

        public async Task<string[]> GetParentsAsync(string commit)
        {
            string output = await RunGitCmdAsync("log -n 1 --format=format:%P \"" + commit + "\"").ConfigureAwait(false);
            return output.Split(' ');
        }

        public async Task<GitRevision[]> GetParentsRevisionsAsync(string commit)
        {
            string[] parents = await GetParentsAsync(commit).ConfigureAwait(false);
            var parentsRevisions = new GitRevision[parents.Length];
            for (int i = 0; i < parents.Length; i++)
                parentsRevisions[i] = await GetRevisionAsync(parents[i], true).ConfigureAwait(false);
            return parentsRevisions;
        }

        public async Task<string> ShowSha1Async(string sha1)
        {
            return ReEncodeShowString(await RunCacheableCmdAsync(AppSettings.GitCommand, "show " + sha1, LosslessEncoding).ConfigureAwait(false));
        }

        public async Task<string> DeleteTagAsync(string tagName)
        {
            return await RunGitCmdAsync(GitCommandHelpers.DeleteTagCmd(tagName)).ConfigureAwait(false);
        }

        public async Task<string> GetCurrentCheckoutAsync()
        {
            return (await RunGitCmdAsync("rev-parse HEAD").ConfigureAwait(false)).TrimEnd();
        }

        public async Task<(bool, string fullSha1)> IsExistingCommitHashAsync(string sha1Fragment)
        {
            string revParseResult = await RunGitCmdAsync(string.Format("rev-parse --verify --quiet {0}^{{commit}}", sha1Fragment)).ConfigureAwait(false);
            revParseResult = revParseResult.Trim();
            if (revParseResult.IsNotNullOrWhitespace() && revParseResult.StartsWith(sha1Fragment))
            {
                return (true, revParseResult);
            }
            else
            {
                return (false, null);
            }
        }

        public async Task<KeyValuePair<char, string>> GetSuperprojectCurrentCheckoutAsync()
        {
            if (SuperprojectModule == null)
                return new KeyValuePair<char, string>(' ', "");

            var lines = (await SuperprojectModule.RunGitCmdAsync("submodule status --cached " + _submodulePath).ConfigureAwait(false)).Split('\n');

            if (lines.Length == 0)
                return new KeyValuePair<char, string>(' ', "");

            string submodule = lines[0];
            if (submodule.Length < 43)
                return new KeyValuePair<char, string>(' ', "");

            var currentCommitGuid = submodule.Substring(1, 40).Trim();
            return new KeyValuePair<char, string>(submodule[0], currentCommitGuid);
        }

        public async Task<bool> ExistsMergeCommitAsync(string startRev, string endRev)
        {
            if (startRev.IsNullOrEmpty() || endRev.IsNullOrEmpty())
                return false;

            string revisions = await RunGitCmdAsync("rev-list --parents --no-walk " + startRev + ".." + endRev).ConfigureAwait(false);
            string[] revisionsTab = revisions.Split('\n');
            Func<string, bool> ex = (string parents) =>
                {
                    string[] tab = parents.Split(' ');
                    return tab.Length > 2 && tab.All(parent => GitRevision.Sha1HashRegex.IsMatch(parent));
                };
            return revisionsTab.Any(ex);
        }

        public ConfigFile GetSubmoduleConfigFile()
        {
            return new ConfigFile(_workingDir + ".gitmodules", true);
        }

        public string GetCurrentSubmoduleLocalPath()
        {
            if (SuperprojectModule == null)
                return null;
            string submodulePath = WorkingDir.Substring(SuperprojectModule.WorkingDir.Length);
            submodulePath = PathUtil.GetDirectoryName(submodulePath.ToPosixPath());
            return submodulePath;
        }

        public string GetSubmoduleNameByPath(string localPath)
        {
            var configFile = GetSubmoduleConfigFile();
            var submodule = configFile.ConfigSections.FirstOrDefault(configSection => configSection.GetValue("path").Trim() == localPath);
            if (submodule != null)
                return submodule.SubSection.Trim();
            return null;
        }

        public string GetSubmoduleRemotePath(string name)
        {
            var configFile = GetSubmoduleConfigFile();
            return configFile.GetPathValue(string.Format("submodule.{0}.url", name)).Trim();
        }

        public string GetSubmoduleFullPath(string localPath)
        {
            if (localPath == null)
            {
                Debug.Assert(true, "No path for submodule - incorrectly parsed status?");
                return "";
            }
            string dir = Path.Combine(_workingDir, localPath.EnsureTrailingPathSeparator());
            return Path.GetFullPath(dir); // fix slashes
        }

        public GitModule GetSubmodule(string localPath)
        {
            return new GitModule(GetSubmoduleFullPath(localPath));
        }

        IGitModule IGitModule.GetSubmodule(string submoduleName)
        {
            return GetSubmodule(submoduleName);
        }

        private GitSubmoduleInfo GetSubmoduleInfo(string submodule)
        {
            var gitSubmodule =
                new GitSubmoduleInfo(this)
                {
                    Initialized = submodule[0] != '-',
                    UpToDate = submodule[0] != '+',
                    CurrentCommitGuid = submodule.Substring(1, 40).Trim()
                };

            var localPath = submodule.Substring(42).Trim();
            if (localPath.Contains("("))
            {
                gitSubmodule.LocalPath = localPath.Substring(0, localPath.IndexOf("(")).TrimEnd();
                gitSubmodule.Branch = localPath.Substring(localPath.IndexOf("(")).Trim(new[] { '(', ')', ' ' });
            }
            else
                gitSubmodule.LocalPath = localPath;
            return gitSubmodule;
        }

        public IEnumerable<IGitSubmoduleInfo> GetSubmodulesInfo()
        {
            var submodules = ReadGitOutputLines("submodule status");

            string lastLine = null;

            foreach (var submodule in submodules)
            {
                if (submodule.Length < 43)
                    continue;

                if (submodule.Equals(lastLine))
                    continue;

                lastLine = submodule;

                yield return GetSubmoduleInfo(submodule);
            }
        }

        public string FindGitSuperprojectPath(out string submoduleName, out string submodulePath)
        {
            submoduleName = null;
            submodulePath = null;
            if (!IsValidGitWorkingDir())
                return null;

            string superprojectPath = null;

            string currentPath = Path.GetDirectoryName(_workingDir); // remove last slash
            if (!string.IsNullOrEmpty(currentPath))
            {
                string path = Path.GetDirectoryName(currentPath);
                for (int i = 0; i < 5; i++)
                {
                    if (string.IsNullOrEmpty(path))
                        break;
                    if (File.Exists(Path.Combine(path, ".gitmodules")) &&
                        IsValidGitWorkingDir(path))
                    {
                        superprojectPath = path.EnsureTrailingPathSeparator();
                        break;
                    }
                    // Check upper directory
                    path = Path.GetDirectoryName(path);
                }
            }

            if (File.Exists(_workingDir + ".git") &&
                superprojectPath == null)
            {
                var lines = File.ReadLines(_workingDir + ".git");
                foreach (string line in lines)
                {
                    if (line.StartsWith("gitdir:"))
                    {
                        string gitpath = line.Substring(7).Trim();
                        int pos = gitpath.IndexOf("/.git/modules/");
                        if (pos != -1)
                        {
                            gitpath = gitpath.Substring(0, pos + 1).Replace('/', '\\');
                            gitpath = Path.GetFullPath(Path.Combine(_workingDir, gitpath));
                            if (File.Exists(gitpath + ".gitmodules") && IsValidGitWorkingDir(gitpath))
                                superprojectPath = gitpath;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(superprojectPath) && currentPath.StartsWith(superprojectPath))
            {
                submodulePath = currentPath.Substring(superprojectPath.Length).ToPosixPath();
                var configFile = new ConfigFile(superprojectPath + ".gitmodules", true);
                foreach (ConfigSection configSection in configFile.ConfigSections)
                {
                    if (configSection.GetValue("path") == submodulePath.ToPosixPath())
                    {
                        submoduleName = configSection.SubSection;
                        return superprojectPath;
                    }
                }
            }

            return null;
        }

        public async Task<string> GetSubmoduleSummaryAsync(string submodule)
        {
            var arguments = string.Format("submodule summary {0}", submodule);
            return await RunGitCmdAsync(arguments).ConfigureAwait(false);
        }

        public async Task<string> ResetSoftAsync(string commit)
        {
            return await ResetSoftAsync(commit, "").ConfigureAwait(false);
        }

        public async Task<string> ResetMixedAsync(string commit)
        {
            return await ResetMixedAsync(commit, "").ConfigureAwait(false);
        }

        public async Task<string> ResetHardAsync(string commit)
        {
            return await ResetHardAsync(commit, "").ConfigureAwait(false);
        }

        public async Task<string> ResetSoftAsync(string commit, string file)
        {
            var args = "reset --soft";

            if (!string.IsNullOrEmpty(commit))
                args += " \"" + commit + "\"";

            if (!string.IsNullOrEmpty(file))
                args += " -- \"" + file + "\"";

            return await RunGitCmdAsync(args).ConfigureAwait(false);
        }

        public async Task<string> ResetMixedAsync(string commit, string file)
        {
            var args = "reset --mixed";

            if (!string.IsNullOrEmpty(commit))
                args += " \"" + commit + "\"";

            if (!string.IsNullOrEmpty(file))
                args += " -- \"" + file + "\"";

            return await RunGitCmdAsync(args).ConfigureAwait(false);
        }

        public async Task<string> ResetHardAsync(string commit, string file)
        {
            var args = "reset --hard";

            if (!string.IsNullOrEmpty(commit))
                args += " \"" + commit + "\"";

            if (!string.IsNullOrEmpty(file))
                args += " -- \"" + file + "\"";

            return await RunGitCmdAsync(args).ConfigureAwait(false);
        }

        public async Task<string> ResetFileAsync(string file)
        {
            file = file.ToPosixPath();
            return await RunGitCmdAsync("checkout-index --index --force -- \"" + file + "\"").ConfigureAwait(false);
        }

        /// <summary>
        /// Determines whether the given repository has index.lock file.
        /// </summary>
        /// <returns><see langword="true"/> is index is locked; otherwise <see langword="false"/>.</returns>
        public bool IsIndexLocked()
        {
            return _indexLockManager.IsIndexLocked();
        }

        /// <summary>
        /// Delete index.lock in the current working folder.
        /// </summary>
        /// <param name="includeSubmodules">
        ///     If <see langword="true"/> all submodules will be scanned for index.lock files and have them delete, if found.
        /// </param>
        /// <exception cref="FileDeleteException">Unable to delete specific index.lock.</exception>
        public void UnlockIndex(bool includeSubmodules)
        {
            _indexLockManager.UnlockIndex(includeSubmodules);
        }

        public async Task<string> FormatPatchAsync(string from, string to, string output, int start)
        {
            output = output.ToPosixPath();

            var result = await RunGitCmdAsync("format-patch -M -C -B --start-number " + start + " \"" + from + "\"..\"" + to +
                                "\" -o \"" + output + "\"").ConfigureAwait(false);

            return result;
        }

        public async Task<string> FormatPatchAsync(string from, string to, string output)
        {
            output = output.ToPosixPath();

            var result = await RunGitCmdAsync("format-patch -M -C -B \"" + from + "\"..\"" + to + "\" -o \"" + output + "\"").ConfigureAwait(false);

            return result;
        }

        public async Task<string> CheckoutFilesAsync(IEnumerable<string> fileList, string revision, bool force)
        {
            string files = fileList.Select(s => s.Quote()).Join(" ");
            if (files.IsNullOrWhiteSpace())
                return string.Empty;

            if (revision == GitRevision.UnstagedGuid)
            {
                Debug.Assert(false, "Unexpectedly reset to unstaged - should be blocked in GUI");
                //Not an error to user, just nothing happens
                return "";
            }

            if (revision == GitRevision.IndexGuid)
            {
                revision = "";
            }
            else 
            {
                revision = revision.QuoteNE();
            }

            return await RunGitCmdAsync("checkout " + force.AsForce() + revision + " -- " + files).ConfigureAwait(false);
        }

        public async Task<string> RemoveFilesAsync(IEnumerable<string> fileList, bool force)
        {
            string files = fileList.Select(s => s.Quote()).Join(" ");
            if (files.IsNullOrWhiteSpace())
                return string.Empty;

            return await RunGitCmdAsync("rm " + force.AsForce() + " -- " + files).ConfigureAwait(false);
        }

        /// <summary>Tries to start Pageant for the specified remote repo (using the remote's PuTTY key file).</summary>
        /// <returns>true if the remote has a PuTTY key file; otherwise, false.</returns>
        public async Task<bool> StartPageantForRemoteAsync(string remote)
        {
            var sshKeyFile = GetPuttyKeyFileForRemote(remote);
            if (string.IsNullOrEmpty(sshKeyFile) || !File.Exists(sshKeyFile))
                return false;

            await StartPageantWithKeyAsync(sshKeyFile).ConfigureAwait(false);
            return true;
        }

        public static async Task StartPageantWithKeyAsync(string sshKeyFile)
        {
            //ensure pageant is loaded, so we can wait for loading a key in the next command
            //otherwise we'll stuck there waiting until pageant exits
            var pageantProcName = Path.GetFileNameWithoutExtension(AppSettings.Pageant);
            if (Process.GetProcessesByName(pageantProcName).Length == 0)
            {
                Process pageantProcess = RunExternalCmdDetached(AppSettings.Pageant, "", "");
                pageantProcess.WaitForInputIdle();
            }

            await GitCommandHelpers.RunCmdAsync(AppSettings.Pageant, "\"" + sshKeyFile + "\"").ConfigureAwait(false);
        }

        public string GetPuttyKeyFileForRemote(string remote)
        {
            if (string.IsNullOrEmpty(remote) ||
                string.IsNullOrEmpty(AppSettings.Pageant) ||
                !AppSettings.AutoStartPageant ||
                !GitCommandHelpers.Plink())
                return "";

            return GetSetting(string.Format("remote.{0}.puttykeyfile", remote));
        }

        public static bool PathIsUrl(string path)
        {
            return path.Contains(Path.DirectorySeparatorChar) || path.Contains(AppSettings.PosixPathSeparator.ToString());
        }

        public async Task<string> FetchCmdAsync(string remote, string remoteBranch, string localBranch, bool? fetchTags = false, bool isUnshallow = false, bool prune = false)
        {
            var progressOption = "";
            if (GitCommandHelpers.VersionInUse.FetchCanAskForProgress)
                progressOption = "--progress ";

            if (string.IsNullOrEmpty(remote) && string.IsNullOrEmpty(remoteBranch) && string.IsNullOrEmpty(localBranch))
                return "fetch " + progressOption;

            return "fetch " + progressOption + await GetFetchArgsAsync(remote, remoteBranch, localBranch, fetchTags, isUnshallow, prune).ConfigureAwait(false);
        }

        public async Task<string> PullCmdAsync(string remote, string remoteBranch, bool rebase, bool? fetchTags = false, bool isUnshallow = false, bool prune = false)
        {
            var pullArgs = "";
            if (GitCommandHelpers.VersionInUse.FetchCanAskForProgress)
                pullArgs = "--progress ";

            if (rebase)
                pullArgs = "--rebase".Combine(" ", pullArgs);

            return "pull " + pullArgs + await GetFetchArgsAsync(remote, remoteBranch, null, fetchTags, isUnshallow, prune && !rebase).ConfigureAwait(false);
        }

        private async Task<string> GetFetchArgsAsync(string remote, string remoteBranch, string localBranch, bool? fetchTags, bool isUnshallow, bool prune)
        {
            remote = remote.ToPosixPath();

            //Remove spaces...
            if (remoteBranch != null)
                remoteBranch = remoteBranch.Replace(" ", "");
            if (localBranch != null)
                localBranch = localBranch.Replace(" ", "");

            string branchArguments = "";

            if (!string.IsNullOrEmpty(remoteBranch))
            {
                if (remoteBranch.StartsWith("+"))
                    remoteBranch = remoteBranch.Remove(0, 1);
                branchArguments = " +" + await FormatBranchNameAsync(remoteBranch).ConfigureAwait(false);

                var remoteUrl = GetSetting(string.Format(SettingKeyString.RemoteUrl, remote));

                if (!string.IsNullOrEmpty(localBranch))
                    branchArguments += ":" + GitCommandHelpers.GetFullBranchName(localBranch);
            }

            string arguments = fetchTags == true ? " --tags" : fetchTags == false ? " --no-tags" : "";

            string pruneArguments = prune ? " --prune" : "";

            if (isUnshallow)
                arguments += " --unshallow";

            return "\"" + remote.Trim() + "\"" + branchArguments + arguments + pruneArguments;
        }

        public string GetRebaseDir()
        {
            string gitDirectory = GetGitDirectory();
            if (Directory.Exists(gitDirectory + "rebase-merge" + Path.DirectorySeparatorChar))
                return gitDirectory + "rebase-merge" + Path.DirectorySeparatorChar;
            if (Directory.Exists(gitDirectory + "rebase-apply" + Path.DirectorySeparatorChar))
                return gitDirectory + "rebase-apply" + Path.DirectorySeparatorChar;
            if (Directory.Exists(gitDirectory + "rebase" + Path.DirectorySeparatorChar))
                return gitDirectory + "rebase" + Path.DirectorySeparatorChar;

            return "";
        }

        /// <summary>Creates a 'git push' command using the specified parameters.</summary>
        /// <param name="remote">Remote repository that is the destination of the push operation.</param>
        /// <param name="force">If a remote ref is not an ancestor of the local ref, overwrite it.
        /// <remarks>This can cause the remote repository to lose commits; use it with care.</remarks></param>
        /// <param name="track">For every branch that is up to date or successfully pushed, add upstream (tracking) reference.</param>
        /// <param name="recursiveSubmodules">If '1', check whether all submodule commits used by the revisions to be pushed are available on a remote tracking branch; otherwise, the push will be aborted.</param>
        /// <returns>'git push' command with the specified parameters.</returns>
        public string PushAllCmd(string remote, ForcePushOptions force, bool track, int recursiveSubmodules)
        {
            remote = remote.ToPosixPath();

            var sforce = GitCommandHelpers.GetForcePushArgument(force);

            var strack = "";
            if (track)
                strack = "-u ";

            var srecursiveSubmodules = "";
            if (recursiveSubmodules == 1)
                srecursiveSubmodules = "--recurse-submodules=check ";
            if (recursiveSubmodules == 2)
                srecursiveSubmodules = "--recurse-submodules=on-demand ";

            var sprogressOption = "";
            if (GitCommandHelpers.VersionInUse.PushCanAskForProgress)
                sprogressOption = "--progress ";

            var options = String.Concat(sforce, strack, srecursiveSubmodules, sprogressOption);
            return String.Format("push {0}--all \"{1}\"", options, remote.Trim());
        }

        /// <summary>Creates a 'git push' command using the specified parameters.</summary>
        /// <param name="remote">Remote repository that is the destination of the push operation.</param>
        /// <param name="fromBranch">Name of the branch to push.</param>
        /// <param name="toBranch">Name of the ref on the remote side to update with the push.</param>
        /// <param name="force">If a remote ref is not an ancestor of the local ref, overwrite it.
        /// <remarks>This can cause the remote repository to lose commits; use it with care.</remarks></param>
        /// <param name="track">For every branch that is up to date or successfully pushed, add upstream (tracking) reference.</param>
        /// <param name="recursiveSubmodules">If '1', check whether all submodule commits used by the revisions to be pushed are available on a remote tracking branch; otherwise, the push will be aborted.</param>
        /// <returns>'git push' command with the specified parameters.</returns>
        public async Task<string> PushCmdAsync(string remote, string fromBranch, string toBranch,
            ForcePushOptions force, bool track, int recursiveSubmodules)
        {
            remote = remote.ToPosixPath();

            // This method is for pushing to remote branches, so fully qualify the
            // remote branch name with refs/heads/.
            fromBranch = await FormatBranchNameAsync(fromBranch).ConfigureAwait(false);
            toBranch = GitCommandHelpers.GetFullBranchName(toBranch);

            if (String.IsNullOrEmpty(fromBranch) && !String.IsNullOrEmpty(toBranch))
                fromBranch = "HEAD";

            if (toBranch != null) toBranch = toBranch.Replace(" ", "");

            var sforce = GitCommandHelpers.GetForcePushArgument(force);

            var strack = "";
            if (track)
                strack = "-u ";

            var srecursiveSubmodules = "";
            if (recursiveSubmodules == 1)
                srecursiveSubmodules = "--recurse-submodules=check ";
            if (recursiveSubmodules == 2)
                srecursiveSubmodules = "--recurse-submodules=on-demand ";

            var sprogressOption = "";
            if (GitCommandHelpers.VersionInUse.PushCanAskForProgress)
                sprogressOption = "--progress ";

            var options = String.Concat(sforce, strack, srecursiveSubmodules, sprogressOption);
            if (!String.IsNullOrEmpty(toBranch) && !String.IsNullOrEmpty(fromBranch))
                return String.Format("push {0}\"{1}\" {2}:{3}", options, remote.Trim(), fromBranch, toBranch);

            return String.Format("push {0}\"{1}\" {2}", options, remote.Trim(), fromBranch);
        }

        private ProcessStartInfo CreateGitStartInfo(string arguments)
        {
            return GitCommandHelpers.CreateProcessStartInfo(AppSettings.GitCommand, arguments, _workingDir, SystemEncoding);
        }

        public string ApplyPatch(string dir, string amCommand)
        {
            var startInfo = CreateGitStartInfo(amCommand);

            using (var process = Process.Start(startInfo))
            {
                var files = Directory.GetFiles(dir);

                if (files.Length == 0)
                    return "";

                foreach (var file in files)
                {
                    using (var fs = new FileStream(file, FileMode.Open))
                    {
                        fs.CopyTo(process.StandardInput.BaseStream);
                    }
                }
                process.StandardInput.Close();
                process.WaitForExit();

                return process.StandardOutput.ReadToEnd().Trim();
            }
        }

        public async Task<(string output, bool wereErrors)> AssumeUnchangedFilesAsync(IList<GitItemStatus> files, bool assumeUnchanged)
        {
            var output = "";
            string error = "";
            bool wereErrors = false;
            var startInfo = CreateGitStartInfo("update-index --" + (assumeUnchanged ? "" : "no-") + "assume-unchanged --stdin");
            var processReader = new Lazy<SynchronizedProcessReader>(() => new SynchronizedProcessReader(Process.Start(startInfo)));

            foreach (var file in files.Where(file => file.IsAssumeUnchanged != assumeUnchanged))
            {
                UpdateIndex(processReader, file.Name);
            }
            if (processReader.IsValueCreated)
            {
                processReader.Value.Process.StandardInput.Close();
                await processReader.Value.WaitForExitAsync().ConfigureAwait(false);
                output = processReader.Value.OutputString(SystemEncoding);
                error = processReader.Value.ErrorString(SystemEncoding);
            }

            return (output.Combine(Environment.NewLine, error), wereErrors);
        }

        public async Task<string> SkipWorktreeFilesAsync(IList<GitItemStatus> files, bool skipWorktree)
        {
            var output = "";
            string error = "";
            var startInfo = CreateGitStartInfo("update-index --" + (skipWorktree ? "" : "no-") + "skip-worktree --stdin");
            var processReader = new Lazy<SynchronizedProcessReader>(() => new SynchronizedProcessReader(Process.Start(startInfo)));

            foreach (var file in files.Where(file => file.IsSkipWorktree != skipWorktree))
            {
                UpdateIndex(processReader, file.Name);
            }
            if (processReader.IsValueCreated)
            {
                processReader.Value.Process.StandardInput.Close();
                await processReader.Value.WaitForExitAsync().ConfigureAwait(false);
                output = processReader.Value.OutputString(SystemEncoding);
                error = processReader.Value.ErrorString(SystemEncoding);
            }

            return output.Combine(Environment.NewLine, error);
        }

        public async Task<(string output, bool wereErrors)> StageFilesAsync(IList<GitItemStatus> files)
        {
            var output = "";
            string error = "";
            bool wereErrors = false;
            var startInfo = CreateGitStartInfo("update-index --add --stdin");
            var processReader = new Lazy<SynchronizedProcessReader>(() => new SynchronizedProcessReader(Process.Start(startInfo)));

            foreach (var file in files.Where(file => !file.IsDeleted))
            {
                UpdateIndex(processReader, file.Name);
            }
            if (processReader.IsValueCreated)
            {
                processReader.Value.Process.StandardInput.Close();
                await processReader.Value.WaitForExitAsync().ConfigureAwait(false);
                wereErrors = processReader.Value.Process.ExitCode != 0;
                output = processReader.Value.OutputString(SystemEncoding);
                error = processReader.Value.ErrorString(SystemEncoding);
            }

            startInfo.Arguments = "update-index --remove --stdin";
            processReader = new Lazy<SynchronizedProcessReader>(() => new SynchronizedProcessReader(Process.Start(startInfo)));
            foreach (var file in files.Where(file => file.IsDeleted))
            {
                UpdateIndex(processReader, file.Name);
            }
            if (processReader.IsValueCreated)
            {
                processReader.Value.Process.StandardInput.Close();
                await processReader.Value.WaitForExitAsync().ConfigureAwait(false);
                output = output.Combine(Environment.NewLine, processReader.Value.OutputString(SystemEncoding));
                error = error.Combine(Environment.NewLine, processReader.Value.ErrorString(SystemEncoding));
                wereErrors = wereErrors || processReader.Value.Process.ExitCode != 0;
            }

            return (output.Combine(Environment.NewLine, error), wereErrors);
        }

        public async Task<string> UnstageFilesAsync(IList<GitItemStatus> files)
        {
            var output = "";
            string error = "";
            var startInfo = CreateGitStartInfo("update-index --info-only --index-info");
            var processReader = new Lazy<SynchronizedProcessReader>(() => new SynchronizedProcessReader(Process.Start(startInfo)));
            foreach (var file in files.Where(file => !file.IsNew))
            {
                await processReader.Value.Process.StandardInput.WriteLineAsync("0 0000000000000000000000000000000000000000\t\"" + file.Name.ToPosixPath() + "\"").ConfigureAwait(false);
            }
            if (processReader.IsValueCreated)
            {
                processReader.Value.Process.StandardInput.Close();
                await processReader.Value.WaitForExitAsync().ConfigureAwait(false);
                output = processReader.Value.OutputString(SystemEncoding);
                error = processReader.Value.ErrorString(SystemEncoding);
            }

            startInfo.Arguments = "update-index --force-remove --stdin";
            processReader = new Lazy<SynchronizedProcessReader>(() => new SynchronizedProcessReader(Process.Start(startInfo)));
            foreach (var file in files.Where(file => file.IsNew))
            {
                UpdateIndex(processReader, file.Name);
            }
            if (processReader.IsValueCreated)
            {
                processReader.Value.Process.StandardInput.Close();
                await processReader.Value.WaitForExitAsync().ConfigureAwait(false);
                output = output.Combine(Environment.NewLine, processReader.Value.OutputString(SystemEncoding));
                error = error.Combine(Environment.NewLine, processReader.Value.ErrorString(SystemEncoding));
            }

            return output.Combine(Environment.NewLine, error);
        }

        private void UpdateIndex(Lazy<SynchronizedProcessReader> processReader, string filename)
        {
            //process.StandardInput.WriteLine("\"" + ToPosixPath(file.Name) + "\"");
            byte[] bytearr = EncodingHelper.ConvertTo(SystemEncoding,
                                                      "\"" + filename.ToPosixPath() + "\"" + processReader.Value.Process.StandardInput.NewLine);
            processReader.Value.Process.StandardInput.BaseStream.Write(bytearr, 0, bytearr.Length);
        }

        public bool InTheMiddleOfBisect()
        {
            return File.Exists(Path.Combine(GetGitDirectory(), "BISECT_START"));
        }

        public bool InTheMiddleOfRebase()
        {
            return !File.Exists(GetRebaseDir() + "applying") &&
                   Directory.Exists(GetRebaseDir());
        }

        public bool InTheMiddleOfPatch()
        {
            return !File.Exists(GetRebaseDir() + "rebasing") &&
                   Directory.Exists(GetRebaseDir());
        }

        public async Task<bool> InTheMiddleOfActionAsync()
        {
            return (await InTheMiddleOfConflictedMergeAsync().ConfigureAwait(false)) || InTheMiddleOfRebase();
        }

        public string GetNextRebasePatch()
        {
            var file = GetRebaseDir() + "next";
            return File.Exists(file) ? File.ReadAllText(file).Trim() : "";
        }

        private static string AppendQuotedString(string str1, string str2)
        {
            var m1 = QuotedText.Match(str1);
            var m2 = QuotedText.Match(str2);
            if (!m1.Success || !m2.Success)
                return str1 + str2;
            Debug.Assert(m1.Groups[1].Value == m2.Groups[1].Value);
            return str1.Substring(0, str1.Length - 2) + m2.Groups[2].Value + "?=";
        }

        private static string DecodeString(string str)
        {
            // decode QuotedPrintable text using .NET internal decoder
            Attachment attachment = Attachment.CreateAttachmentFromString("", str);
            return attachment.Name;
        }

        private static readonly Regex HeadersMatch = new Regex(@"^(?<header_key>[-A-Za-z0-9]+)(?::[ \t]*)(?<header_value>.*)$", RegexOptions.Compiled);
        private static readonly Regex QuotedText = new Regex(@"=\?([\w-]+)\?q\?(.*)\?=$", RegexOptions.Compiled);

        public bool InTheMiddleOfInteractiveRebase()
        {
            return File.Exists(GetRebaseDir() + "git-rebase-todo");
        }

        public async Task<IList<PatchFile>> GetInteractiveRebasePatchFilesAsync()
        {
            string todoFile = GetRebaseDir() + "git-rebase-todo";
            string[] todoCommits = File.Exists(todoFile) ? File.ReadAllText(todoFile).Trim().Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries) : null;

            IList<PatchFile> patchFiles = new List<PatchFile>();

            if (todoCommits != null)
            {
                string commentChar = EffectiveConfigFile.GetString("core.commentChar", "#");

                foreach (string todoCommit in todoCommits)
                {
                    if (todoCommit.StartsWith(commentChar))
                        continue;

                    string[] parts = todoCommit.Split(' ');

                    if (parts.Length >= 3)
                    {
                        (CommitData data, string error) = await _commitDataManager.GetCommitDataAsync(parts[1]).ConfigureAwait(false);

                        PatchFile nextCommitPatch = new PatchFile();
                        nextCommitPatch.Author = string.IsNullOrEmpty(error) ? data.Author : error;
                        nextCommitPatch.Subject = string.IsNullOrEmpty(error) ? data.Body : error;
                        nextCommitPatch.Name = parts[0];
                        nextCommitPatch.Date = string.IsNullOrEmpty(error) ? data.CommitDate.LocalDateTime.ToString() : error;
                        nextCommitPatch.IsNext = patchFiles.Count == 0;

                        patchFiles.Add(nextCommitPatch);
                    }
                }
            }

            return patchFiles;
        }

        public IList<PatchFile> GetRebasePatchFiles()
        {
            var patchFiles = new List<PatchFile>();

            var nextFile = GetNextRebasePatch();

            int next;
            int.TryParse(nextFile, out next);


            var files = new string[0];
            if (Directory.Exists(GetRebaseDir()))
                files = Directory.GetFiles(GetRebaseDir());

            foreach (var fullFileName in files)
            {
                int n;
                var file = PathUtil.GetFileName(fullFileName);
                if (!int.TryParse(file, out n))
                    continue;

                var patchFile =
                    new PatchFile
                        {
                            Name = file,
                            FullName = fullFileName,
                            IsNext = n == next,
                            IsSkipped = n < next
                        };

                if (File.Exists(GetRebaseDir() + file))
                {
                    string key = null;
                    string value = "";
                    foreach (var line in File.ReadLines(GetRebaseDir() + file))
                    {
                        var m = HeadersMatch.Match(line);
                        if (key == null)
                        {
                            if (!string.IsNullOrWhiteSpace(line) && !m.Success)
                                continue;
                        }
                        else if (string.IsNullOrWhiteSpace(line) || m.Success)
                        {
                            value = DecodeString(value);
                            switch (key)
                            {
                                case "From":
                                    if (value.IndexOf('<') > 0 && value.IndexOf('<') < value.Length)
                                    {
                                        var author = RFC2047Decoder.Parse(value);
                                        patchFile.Author = author.Substring(0, author.IndexOf('<')).Trim();
                                    }
                                    else
                                        patchFile.Author = value;
                                    break;
                                case "Date":
                                    if (value.IndexOf('+') > 0 && value.IndexOf('<') < value.Length)
                                        patchFile.Date = value.Substring(0, value.IndexOf('+')).Trim();
                                    else
                                        patchFile.Date = value;
                                    break;
                                case "Subject":
                                    patchFile.Subject = value;
                                    break;
                            }
                        }
                        if (m.Success)
                        {
                            key = m.Groups[1].Value;
                            value = m.Groups[2].Value;
                        }
                        else
                            value = AppendQuotedString(value, line.Trim());

                        if (string.IsNullOrEmpty(line) ||
                            !string.IsNullOrEmpty(patchFile.Author) &&
                            !string.IsNullOrEmpty(patchFile.Date) &&
                            !string.IsNullOrEmpty(patchFile.Subject))
                            break;
                    }
                }

                patchFiles.Add(patchFile);
            }

            return patchFiles;
        }

        public string CommitCmd(bool amend, bool signOff = false, string author = "", bool useExplicitCommitMessage = true, bool noVerify = false, bool gpgSign = false, string gpgKeyId = "")
        {
            string command = "commit";
            if (amend)
                command += " --amend";

            if (noVerify)
                command += " --no-verify";

            if (signOff)
                command += " --signoff";

            if (!string.IsNullOrEmpty(author))
            {
                author = author.Trim().Trim('"');
                command += " --author=\"" + author + "\"";
            }                

            if (gpgSign)
            {
                command += " -S";

                if (!string.IsNullOrWhiteSpace(gpgKeyId))
                    command += gpgKeyId;
            }

            if (useExplicitCommitMessage)
            {
                var path = Path.Combine(GetGitDirectory(), "COMMITMESSAGE");
                command += " -F \"" + path + "\"";
            }

            return command;
        }

        /// <summary>
        /// Removes the registered remote by running <c>git remote rm</c> command.
        /// </summary>
        /// <param name="remoteName">The remote name.</param>
        public async Task<string> RemoveRemoteAsync(string remoteName)
        {
            return await RunGitCmdAsync("remote rm \"" + remoteName + "\"").ConfigureAwait(false);
        }

        /// <summary>
        /// Renames the registered remote by running <c>git remote rename</c> command.
        /// </summary>
        /// <param name="remoteName">The current remote name.</param>
        /// <param name="newName">The new remote name.</param>
        public async Task<string> RenameRemoteAsync(string remoteName, string newName)
        {
            return await RunGitCmdAsync("remote rename \"" + remoteName + "\" \"" + newName + "\"").ConfigureAwait(false);
        }

        public async Task<string> RenameBranchAsync(string name, string newName)
        {
            return await RunGitCmdAsync("branch -m \"" + name + "\" \"" + newName + "\"").ConfigureAwait(false);
        }

        public async Task<string> AddRemoteAsync(string name, string path)
        {
            var location = path.ToPosixPath();

            if (string.IsNullOrEmpty(name))
                return "Please enter a name.";

            return
                string.IsNullOrEmpty(location)
                    ? await RunGitCmdAsync(string.Format("remote add \"{0}\" \"\"", name)).ConfigureAwait(false)
                    : await RunGitCmdAsync(string.Format("remote add \"{0}\" \"{1}\"", name, location)).ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves registered remotes by running <c>git remote show</c> command.
        /// </summary>
        /// <returns>Registered remotes.</returns>
        public async Task<string[]> GetRemotesAsync()
        {
            return await GetRemotesAsync(true).ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves registered remotes by running <c>git remote show</c> command.
        /// </summary>
        /// <param name="allowEmpty"></param>
        /// <returns>Registered remotes.</returns>
        public async Task<string[]> GetRemotesAsync(bool allowEmpty)
        {
            string remotes = await RunGitCmdAsync("remote show").ConfigureAwait(false);
            return allowEmpty ? remotes.Split('\n') : remotes.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public IEnumerable<string> GetSettings(string setting)
        {
            return LocalConfigFile.GetValues(setting);
        }

        public string GetSetting(string setting)
        {
            return LocalConfigFile.GetValue(setting);
        }

        public string GetEffectiveSetting(string setting)
        {
            return EffectiveConfigFile.GetValue(setting);
        }

        public void UnsetSetting(string setting)
        {
            SetSetting(setting, null);
        }

        public void SetSetting(string setting, string value)
        {
            LocalConfigFile.SetValue(setting, value);
        }

        public void SetPathSetting(string setting, string value)
        {
            LocalConfigFile.SetPathValue(setting, value);
        }

        public async Task<IList<GitStash>> GetStashesAsync()
        {
            var list = (await RunGitCmdAsync("stash list").ConfigureAwait(false)).Split('\n');

            var stashes = new List<GitStash>();
            for (int i = 0; i < list.Length; i++)
            {
                string stashString = list[i];
                if (stashString.IndexOf(':') > 0 && !stashString.StartsWith("fatal: "))
                {
                    stashes.Add(new GitStash(stashString, i));
                }
            }

            return stashes;
        }

        public async Task<Patch> GetSingleDiffAsync(string firstRevision, string secondRevision, string fileName, string oldFileName, string extraDiffArguments, Encoding encoding, bool cacheResult, bool isTracked = true)
        {
            if (!string.IsNullOrEmpty(fileName))
            {
                fileName = fileName.ToPosixPath();
            }
            if (!string.IsNullOrEmpty(oldFileName))
            {
                oldFileName = oldFileName.ToPosixPath();
            }

            //fix refs slashes
            firstRevision = firstRevision?.ToPosixPath();
            secondRevision = secondRevision?.ToPosixPath();
            string diffOptions = _revisionDiffProvider.Get(firstRevision, secondRevision, fileName, oldFileName, isTracked);
            if (AppSettings.UsePatienceDiffAlgorithm)
                extraDiffArguments = string.Concat(extraDiffArguments, " --patience");

            var patchManager = new PatchManager();
            var arguments = String.Format(DiffCommandWithStandardArgs + "{0} -M -C {1}", extraDiffArguments, diffOptions);
            cacheResult = cacheResult &&
                !secondRevision.IsArtificial() &&
                !firstRevision.IsArtificial() &&
                !secondRevision.IsNullOrEmpty() &&
                !firstRevision.IsNullOrEmpty();
            string patch;
            if (cacheResult)
                patch = await RunCacheableCmdAsync(AppSettings.GitCommand, arguments, LosslessEncoding).ConfigureAwait(false);
            else
                patch = await RunCmdAsync(AppSettings.GitCommand, arguments, LosslessEncoding).ConfigureAwait(false);
            patchManager.LoadPatch(patch, false, encoding);

            return GetPatch(patchManager, fileName, oldFileName);
        }

        private Patch GetPatch(PatchApply.PatchManager patchManager, string fileName, string oldFileName)
        {
            foreach (Patch p in patchManager.Patches)
                if (fileName == p.FileNameB &&
                    (fileName == p.FileNameA || oldFileName == p.FileNameA))
                    return p;

            return patchManager.Patches.Count > 0 ? patchManager.Patches[patchManager.Patches.Count - 1] : null;
        }

        public async Task<string> GetStatusTextAsync(bool untracked)
        {
            string cmd = "status -s";
            if (untracked)
                cmd = cmd + " -u";
            return await RunGitCmdAsync(cmd).ConfigureAwait(false);
        }

        public async Task<string> GetDiffFilesTextAsync(string firstRevision, string secondRevision)
        {
            return await GetDiffFilesTextAsync(firstRevision, secondRevision, false).ConfigureAwait(false);
        }

        public async Task<string> GetDiffFilesTextAsync(string firstRevision, string secondRevision, bool noCache)
        {
            string cmd = DiffCommandWithStandardArgs + "-M -C --name-status " + _revisionDiffProvider.Get(firstRevision, secondRevision);
            return noCache ? await RunGitCmdAsync(cmd).ConfigureAwait(false) : await this.RunCacheableCmdAsync(AppSettings.GitCommand, cmd, SystemEncoding).ConfigureAwait(false);
        }

        public async Task<List<GitItemStatus>> GetDiffFilesWithSubmodulesStatusAsync(string firstRevision, string secondRevision)
        {
            var status = await GetDiffFilesAsync(firstRevision, secondRevision).ConfigureAwait(false);
            GetSubmoduleStatus(status, firstRevision, secondRevision);
            return status;
        }

        public async Task<List<GitItemStatus>> GetDiffFilesAsync(string firstRevision, string secondRevision, bool noCache = false)
        {
            noCache = noCache || firstRevision.IsArtificial() || secondRevision.IsArtificial();
            string cmd = DiffCommandWithStandardArgs + "-M -C -z --name-status " + _revisionDiffProvider.Get(firstRevision, secondRevision);
            string result = noCache ? await RunGitCmdAsync(cmd).ConfigureAwait(false) : await this.RunCacheableCmdAsync(AppSettings.GitCommand, cmd, SystemEncoding).ConfigureAwait(false);
            var resultCollection = GitCommandHelpers.GetAllChangedFilesFromString(this, result, true);
            if (firstRevision == GitRevision.UnstagedGuid || secondRevision == GitRevision.UnstagedGuid)
            {
                //For unstaged the untracked must be added too
                var files = (await GetUnstagedFilesWithSubmodulesStatusAsync().ConfigureAwait(false)).Where(item => item.IsNew);
                if (firstRevision == GitRevision.UnstagedGuid)
                {
                    //The file is seen as "deleted" in 'to' revision
                    foreach (var item in files)
                    {
                        item.IsNew = false;
                        item.IsDeleted = true;
                        resultCollection.Add(item);
                    }
                }
                else
                {
                    resultCollection.AddRange(files);
                }
            }
            return resultCollection;
        }

        public async Task<IList<GitItemStatus>> GetStashDiffFilesAsync(string stashName)
        {
            var resultCollection = await GetDiffFilesAsync(stashName + "^", stashName, true).ConfigureAwait(false);

            // shows untracked files
            string untrackedTreeHash = await RunGitCmdAsync("log " + stashName + "^3 --pretty=format:\"%T\" --max-count=1").ConfigureAwait(false);
            if (GitRevision.Sha1HashRegex.IsMatch(untrackedTreeHash))
            {
                var files = await GetTreeFilesAsync(untrackedTreeHash, true).ConfigureAwait(false);
                resultCollection.AddRange(files);
            }

            return resultCollection;
        }

        public async Task<IList<GitItemStatus>> GetTreeFilesAsync(string treeGuid, bool full)
        {
            var tree = await GetTreeAsync(treeGuid, full).ConfigureAwait(false);

            var list = tree
                .Select(file => new GitItemStatus
                {
                    IsTracked = true,
                    IsNew = true,
                    IsChanged = false,
                    IsDeleted = false,
                    IsStaged = false,
                    Name = file.Name,
                    TreeGuid = file.Guid
                }).ToList();

            // Doesn't work with removed submodules
            var submodulesList = GetSubmodulesLocalPaths();
            foreach (var item in list)
            {
                if (submodulesList.Contains(item.Name))
                    item.IsSubmodule = true;
            }

            return list;
        }

        public async Task<IList<GitItemStatus>> GetAllChangedFilesAsync(bool excludeIgnoredFiles = true,
            bool excludeAssumeUnchangedFiles = true, bool excludeSkipWorktreeFiles = true,
            UntrackedFilesMode untrackedFiles = UntrackedFilesMode.Default)
        {
            var status = await RunGitCmdAsync(GitCommandHelpers.GetAllChangedFilesCmd(excludeIgnoredFiles, untrackedFiles)).ConfigureAwait(false);
            List<GitItemStatus> result = GitCommandHelpers.GetAllChangedFilesFromString(this, status);

            if (!excludeAssumeUnchangedFiles || !excludeSkipWorktreeFiles)
            {
                string lsOutput = await RunGitCmdAsync("ls-files -v").ConfigureAwait(false);
                if (!excludeAssumeUnchangedFiles)
                    result.AddRange(GitCommandHelpers.GetAssumeUnchangedFilesFromString(lsOutput));
                if (!excludeSkipWorktreeFiles)
                    result.AddRange(GitCommandHelpers.GetSkipWorktreeFilesFromString(lsOutput));
            }

            return result;
        }

        public async Task<IList<GitItemStatus>> GetAllChangedFilesWithSubmodulesStatusAsync(bool excludeIgnoredFiles = true,
            bool excludeAssumeUnchangedFiles = true, bool excludeSkipWorktreeFiles = true,
            UntrackedFilesMode untrackedFiles = UntrackedFilesMode.Default)
        {
            var status = await GetAllChangedFilesAsync(excludeIgnoredFiles, excludeAssumeUnchangedFiles, excludeSkipWorktreeFiles, untrackedFiles).ConfigureAwait(false);
            GetCurrentSubmoduleStatus(status);
            return status;
        }

        private void GetCurrentSubmoduleStatus(IList<GitItemStatus> status)
        {
            foreach (var item in status)
                if (item.IsSubmodule)
                {
                    var localItem = item;
                    localItem.SubmoduleStatus = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await TaskScheduler.Default.SwitchTo(alwaysYield: true);

                        var submoduleStatus = await GitCommandHelpers.GetCurrentSubmoduleChangesAsync(this, localItem.Name, localItem.OldName, localItem.IsStaged).ConfigureAwait(false);
                        if (submoduleStatus != null && submoduleStatus.Commit != submoduleStatus.OldCommit)
                        {
                            var submodule = submoduleStatus.GetSubmodule(this);
                            await submoduleStatus.CheckSubmoduleStatusAsync(submodule).ConfigureAwait(false);
                        }
                        return submoduleStatus;
                    });
                }
        }

        private void GetSubmoduleStatus(IList<GitItemStatus> status, string firstRevision, string secondRevision)
        {
            status.ForEach(item =>
            {
                if (item.IsSubmodule)
                {
                    item.SubmoduleStatus = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await TaskScheduler.Default.SwitchTo(alwaysYield: true);

                        Patch patch = await GetSingleDiffAsync(firstRevision, secondRevision, item.Name, item.OldName, "", SystemEncoding, true).ConfigureAwait(false);
                        string text = patch != null ? patch.Text : "";
                        var submoduleStatus = await GitCommandHelpers.GetSubmoduleStatusAsync(text, this, item.Name).ConfigureAwait(false);
                        if (submoduleStatus.Commit != submoduleStatus.OldCommit)
                        {
                            var submodule = submoduleStatus.GetSubmodule(this);
                            await submoduleStatus.CheckSubmoduleStatusAsync(submodule).ConfigureAwait(false);
                        }
                        return submoduleStatus;
                    });
                }
            });
        }

        public async Task<IList<GitItemStatus>> GetStagedFilesAsync()
        {
            string status = await RunGitCmdAsync(DiffCommandWithStandardArgs + "-M -C -z --cached --name-status", SystemEncoding).ConfigureAwait(false);

            if (status.Length < 50 && status.Contains("fatal: No HEAD commit to compare"))
            {
                //This command is a little more expensive because it will return both staged and unstaged files
                string command = GitCommandHelpers.GetAllChangedFilesCmd(true, UntrackedFilesMode.No);
                status = await RunGitCmdAsync(command, SystemEncoding).ConfigureAwait(false);
                IList<GitItemStatus> stagedFiles = GitCommandHelpers.GetAllChangedFilesFromString(this, status, false);
                return stagedFiles.Where(f => f.IsStaged).ToList();
            }

            return GitCommandHelpers.GetAllChangedFilesFromString(this, status, true);
        }

        public async Task<IList<GitItemStatus>> GetStagedFilesWithSubmodulesStatusAsync()
        {
            var status = await GetStagedFilesAsync().ConfigureAwait(false);
            GetCurrentSubmoduleStatus(status);
            return status;
        }

        public async Task<IList<GitItemStatus>> GetUnstagedFilesAsync()
        {
            return (await GetAllChangedFilesAsync().ConfigureAwait(false)).Where(x => !x.IsStaged).ToArray();
        }

        public async Task<IList<GitItemStatus>> GetUnstagedFilesWithSubmodulesStatusAsync()
        {
            return (await GetAllChangedFilesWithSubmodulesStatusAsync().ConfigureAwait(false)).Where(x => !x.IsStaged).ToArray();
        }

        public async Task<IList<GitItemStatus>> GitStatusAsync(UntrackedFilesMode untrackedFilesMode, IgnoreSubmodulesMode ignoreSubmodulesMode = 0)
        {
            string command = GitCommandHelpers.GetAllChangedFilesCmd(true, untrackedFilesMode, ignoreSubmodulesMode);
            string status = await RunGitCmdAsync(command).ConfigureAwait(false);
            return GitCommandHelpers.GetAllChangedFilesFromString(this, status);
        }

        /// <summary>Indicates whether there are any changes to the repository,
        ///  including any untracked files or directories; excluding submodules.</summary>
        public async Task<bool> IsDirtyDirAsync()
        {
            return (await GitStatusAsync(UntrackedFilesMode.All, IgnoreSubmodulesMode.Default).ConfigureAwait(false)).Count > 0;
        }

        public async Task<Patch> GetCurrentChangesAsync(string fileName, string oldFileName, bool staged, string extraDiffArguments, Encoding encoding)
        {
            fileName = fileName.ToPosixPath();
            if (!string.IsNullOrEmpty(oldFileName))
                oldFileName = oldFileName.ToPosixPath();

            if (AppSettings.UsePatienceDiffAlgorithm)
                extraDiffArguments = string.Concat(extraDiffArguments, " --patience");

            var args = string.Concat(DiffCommandWithStandardArgs, extraDiffArguments, " -- ", fileName.Quote());
            if (staged)
                args = string.Concat(DiffCommandWithStandardArgs, "-M -C --cached ", extraDiffArguments, " -- ", fileName.Quote(), " ", oldFileName.Quote());

            String result = await RunGitCmdAsync(args, LosslessEncoding).ConfigureAwait(false);
            var patchManager = new PatchManager();
            patchManager.LoadPatch(result, false, encoding);

            return GetPatch(patchManager, fileName, oldFileName);
        }

        private async Task<string> GetFileContentsAsync(string path)
        {
            var contents = await RunGitCmdResultAsync(string.Format("show HEAD:\"{0}\"", path.ToPosixPath())).ConfigureAwait(false);
            if (contents.ExitCode == 0)
                return contents.StdOutput;

            return null;
        }

        public async Task<string> GetFileContentsAsync(GitItemStatus file)
        {
            var contents = new StringBuilder();

            string currentContents = await GetFileContentsAsync(file.Name).ConfigureAwait(false);
            if (currentContents != null)
                contents.Append(currentContents);

            if (file.OldName != null)
            {
                string oldContents = await GetFileContentsAsync(file.OldName).ConfigureAwait(false);
                if (oldContents != null)
                    contents.Append(oldContents);
            }

            return contents.Length > 0 ? contents.ToString() : null;
        }

        public async Task<string> StageFileAsync(string file)
        {
            return await RunGitCmdAsync("update-index --add" + " \"" + file.ToPosixPath() + "\"").ConfigureAwait(false);
        }

        public async Task<string> StageFileToRemoveAsync(string file)
        {
            return await RunGitCmdAsync("update-index --remove" + " \"" + file.ToPosixPath() + "\"").ConfigureAwait(false);
        }

        public async Task<string> UnstageFileAsync(string file)
        {
            return await RunGitCmdAsync("rm --cached \"" + file.ToPosixPath() + "\"").ConfigureAwait(false);
        }

        public async Task<string> UnstageFileToRemoveAsync(string file)
        {
            return await RunGitCmdAsync("reset HEAD -- \"" + file.ToPosixPath() + "\"").ConfigureAwait(false);
        }

        /// <summary>Dirty but fast. This sometimes fails.</summary>
        public static string GetSelectedBranchFast(string repositoryPath)
        {
            if (string.IsNullOrEmpty(repositoryPath))
                return string.Empty;

            string head;
            string headFileName = Path.Combine(GetGitDirectory(repositoryPath), "HEAD");
            if (File.Exists(headFileName))
            {
                head = File.ReadAllText(headFileName, SystemEncoding);
                if (!head.Contains("ref:"))
                    return DetachedBranch;
            }
            else
            {
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(head))
            {
                return head.Replace("ref:", "").Replace("refs/heads/", string.Empty).Trim();
            }

            return string.Empty;
        }

        /// <summary>Gets the current branch; or "(no branch)" if HEAD is detached.</summary>
        public async Task<string> GetSelectedBranchAsync(string repositoryPath)
        {
            string head = GetSelectedBranchFast(repositoryPath);

            if (string.IsNullOrEmpty(head))
            {
                var result = await RunGitCmdResultAsync("symbolic-ref HEAD").ConfigureAwait(false);
                if (result.ExitCode == 1)
                    return DetachedBranch;
                return result.StdOutput;
            }

            return head;
        }

        /// <summary>Gets the current branch; or "(no branch)" if HEAD is detached.</summary>
        public async Task<string> GetSelectedBranchAsync()
        {
            return await GetSelectedBranchAsync(_workingDir).ConfigureAwait(false);
        }

        /// <summary>Indicates whether HEAD is not pointing to a branch.</summary>
        public async Task<bool> IsDetachedHeadAsync()
        {
            return IsDetachedHead(await GetSelectedBranchAsync().ConfigureAwait(false));
        }

        public static bool IsDetachedHead(string branch)
        {
            return DetachedPrefixes.Any(a => branch.StartsWith(a, StringComparison.Ordinal));
        }

        /// <summary>Gets the remote of the current branch; or "origin" if no remote is configured.</summary>
        public async Task<string> GetCurrentRemoteAsync()
        {
            string remote = GetSetting(string.Format(SettingKeyString.BranchRemote, await GetSelectedBranchAsync().ConfigureAwait(false)));
            return remote;
        }

        /// <summary>Gets the remote branch of the specified local branch; or "" if none is configured.</summary>
        public string GetRemoteBranch(string branch)
        {
            string remote = GetSetting(string.Format(SettingKeyString.BranchRemote, branch));
            string merge = GetSetting(string.Format("branch.{0}.merge", branch));
            if (String.IsNullOrEmpty(remote) || String.IsNullOrEmpty(merge))
                return "";
            return remote + "/" + (merge.StartsWith("refs/heads/") ? merge.Substring(11) : merge);
        }

        public async Task<IEnumerable<IGitRef>> GetRemoteBranchesAsync()
        {
            return (await GetRefsAsync().ConfigureAwait(false)).Where(r => r.IsRemote);
        }

        public async Task<RemoteActionResult<IList<IGitRef>>> GetRemoteServerRefsAsync(string remote, bool tags, bool branches)
        {
            var result = new RemoteActionResult<IList<IGitRef>>()
            {
                AuthenticationFail = false,
                HostKeyFail = false,
                Result = null
            };

            remote = remote.ToPosixPath();

            result.CmdResult = await GetTreeFromRemoteRefsAsync(remote, tags, branches).ConfigureAwait(false);

            var tree = result.CmdResult.StdOutput;
            // If the authentication failed because of a missing key, ask the user to supply one.
            if (tree.Contains("FATAL ERROR") && tree.Contains("authentication"))
            {
                result.AuthenticationFail = true;
            }
            else if (tree.ToLower().Contains("the server's host key is not cached in the registry"))
            {
                result.HostKeyFail = true;
            }
            else if (result.CmdResult.ExitedSuccessfully)
            {
                result.Result = await GetTreeRefsAsync(tree).ConfigureAwait(false);
            }

            return result;
        }

        private async Task<CmdResult> GetTreeFromRemoteRefsExAsync(string remote, bool tags, bool branches)
        {
            if (tags && branches)
                return await RunGitCmdResultAsync("ls-remote --heads --tags \"" + remote + "\"").ConfigureAwait(false);
            if (tags)
                return await RunGitCmdResultAsync("ls-remote --tags \"" + remote + "\"").ConfigureAwait(false);
            if (branches)
                return await RunGitCmdResultAsync("ls-remote --heads \"" + remote + "\"").ConfigureAwait(false);
            return new CmdResult();
        }

        private async Task<CmdResult> GetTreeFromRemoteRefsAsync(string remote, bool tags, bool branches)
        {
            return await GetTreeFromRemoteRefsExAsync(remote, tags, branches).ConfigureAwait(false);
        }

        public async Task<IList<IGitRef>> GetRefsAsync(bool tags = true, bool branches = true)
        {
            var tree = await GetTreeAsync(tags, branches).ConfigureAwait(false);
            return await GetTreeRefsAsync(tree).ConfigureAwait(false);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="option">Ordery by date is slower.</param>
        /// <returns></returns>
        public async Task<IList<IGitRef>> GetTagRefsAsync(GetTagRefsSortOrder option)
        {
            var list = await GetRefsAsync(true, false).ConfigureAwait(false);

            List<IGitRef> sortedList;
            if (option == GetTagRefsSortOrder.ByCommitDateAscending)
            {
                sortedList = list.OrderBy(head =>
                {
                    var r = new GitRevision(head.Guid);
                    return r.CommitDate;
                }).ToList();
            }
            else if (option == GetTagRefsSortOrder.ByCommitDateDescending)
            {
                sortedList = list.OrderByDescending(head =>
                {
                    var r = new GitRevision(head.Guid);
                    return r.CommitDate;
                }).ToList();
            }
            else
                sortedList = new List<IGitRef>(list);

            return sortedList;
        }

        public enum GetTagRefsSortOrder
        {
            /// <summary>
            /// default
            /// </summary>
            ByName,

            /// <summary>
            /// slower than ByName
            /// </summary>
            ByCommitDateAscending,

            /// <summary>
            /// slower than ByName
            /// </summary>
            ByCommitDateDescending
        }

        public async Task<ICollection<string>> GetMergedBranchesAsync(bool includeRemote = false)
        {
            return (await RunGitCmdAsync(GitCommandHelpers.MergedBranches(includeRemote)).ConfigureAwait(false)).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public async Task<ICollection<string>> GetMergedRemoteBranchesAsync()
        {
            string remoteBranchPrefixForMergedBranches = "remotes/";
            string refsPrefix = "refs/";

            string[] mergedBranches = (await RunGitCmdAsync(GitCommandHelpers.MergedBranches(includeRemote: true)).ConfigureAwait(false)).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var remotes = await GetRemotesAsync(allowEmpty: false).ConfigureAwait(false);

            return mergedBranches
                .Select(b => b.Trim())
                .Where(b => b.StartsWith(remoteBranchPrefixForMergedBranches))
                .Select(b => string.Concat(refsPrefix, b))
                .Where(b => !string.IsNullOrEmpty(GitCommandHelpers.GetRemoteName(b, remotes))).ToList();
        }

        private async Task<string> GetTreeAsync(bool tags, bool branches)
        {
            if (tags && branches)
                return await RunGitCmdAsync("show-ref --dereference", SystemEncoding).ConfigureAwait(false);

            if (tags)
                return await RunGitCmdAsync("show-ref --tags", SystemEncoding).ConfigureAwait(false);

            if (branches)
                return await RunGitCmdAsync(@"for-each-ref --sort=-committerdate refs/heads/ --format=""%(objectname) %(refname)""", SystemEncoding).ConfigureAwait(false);

            return "";
        }

        public async Task<IList<IGitRef>> GetTreeRefsAsync(string tree)
        {
            var itemsStrings = tree.Split('\n');

            var gitRefs = new List<IGitRef>();
            var defaultHeads = new Dictionary<string, GitRef>(); // remote -> HEAD
            var remotes = await GetRemotesAsync(false).ConfigureAwait(false);

            foreach (var itemsString in itemsStrings)
            {
                if (itemsString == null || itemsString.Length <= 42 || itemsString.StartsWith("error: "))
                    continue;

                var completeName = itemsString.Substring(41).Trim();
                var guid = itemsString.Substring(0, 40);
                if (GitRevision.IsFullSha1Hash(guid) && completeName.StartsWith("refs/"))
                {
                    var remoteName = GitCommandHelpers.GetRemoteName(completeName, remotes);
                    var head = new GitRef(this, guid, completeName, remoteName);
                    if (DefaultHeadPattern.IsMatch(completeName))
                        defaultHeads[remoteName] = head;
                    else
                        gitRefs.Add(head);
                }
            }

            // do not show default head if remote has a branch on the same commit
            GitRef defaultHead;
            foreach (var gitRef in gitRefs.Where(head => defaultHeads.TryGetValue(head.Remote, out defaultHead) && head.Guid == defaultHead.Guid))
            {
                defaultHeads.Remove(gitRef.Remote);
            }

            gitRefs.AddRange(defaultHeads.Values);

            return gitRefs;
        }

        /// <summary>
        /// Gets branches which contain the given commit.
        /// If both local and remote branches are requested, remote branches are prefixed with "remotes/"
        /// (as returned by git branch -a)
        /// </summary>
        /// <param name="sha1">The sha1.</param>
        /// <param name="getLocal">Pass true to include local branches</param>
        /// <param name="getRemote">Pass true to include remote branches</param>
        /// <returns></returns>
        public async Task<IEnumerable<string>> GetAllBranchesWhichContainGivenCommitAsync(string sha1, bool getLocal, bool getRemote)
        {
            string args = "--contains " + sha1;
            if (getRemote && getLocal)
                args = "-a " + args;
            else if (getRemote)
                args = "-r " + args;
            else if (!getLocal)
                return new string[] { };
            string info = await RunGitCmdAsync("branch " + args).ConfigureAwait(false);
            if (info.Trim().StartsWith("fatal") || info.Trim().StartsWith("error:"))
                return new List<string>();

            string[] result = info.Split(new[] { '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries);

            // Remove symlink targets as in "origin/HEAD -> origin/master"
            for (int i = 0; i < result.Length; i++)
            {
                string item = result[i].Trim();
                int idx;
                if (getRemote && ((idx = item.IndexOf(" ->")) >= 0))
                {
                    item = item.Substring(0, idx);
                }
                result[i] = item;
            }

            return result;
        }

        /// <summary>
        /// Gets all tags which contain the given commit.
        /// </summary>
        /// <param name="sha1">The sha1.</param>
        /// <returns></returns>
        public async Task<IEnumerable<string>> GetAllTagsWhichContainGivenCommitAsync(string sha1)
        {
            string info = await RunGitCmdAsync("tag --contains " + sha1, SystemEncoding).ConfigureAwait(false);

            if (info.Trim().StartsWith("fatal") || info.Trim().StartsWith("error:"))
                return new List<string>();
            return info.Split(new[] { '\r', '\n', '*', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Returns tag's message. If the lightweight tag is passed, corresponding commit message
        /// is returned.
        /// </summary>
        public async Task<string> GetTagMessageAsync(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            tag = tag.Trim();

            string info = await RunGitCmdAsync("tag -l -n10 " + tag, SystemEncoding).ConfigureAwait(false);

            if (info.Trim().StartsWith("fatal") || info.Trim().StartsWith("error:"))
                return null;

            if (!info.StartsWith(tag))
                return null;

            info = info.Substring(tag.Length).Trim();
            if (info.Length == 0)
                return null;

            return info;
        }

        /// <summary>
        /// Returns list of filenames which would be ignored
        /// </summary>
        /// <param name="ignorePatterns">Patterns to ignore (.gitignore syntax)</param>
        /// <returns></returns>
        public async Task<IList<string>> GetIgnoredFilesAsync(IEnumerable<string> ignorePatterns)
        {
            var notEmptyPatterns = ignorePatterns
                    .Where(pattern => !pattern.IsNullOrWhiteSpace());
            if (notEmptyPatterns.Count() != 0)
            {
                var excludeParams =
                    notEmptyPatterns
                    .Select(pattern => "-x " + pattern.Quote())
                    .Join(" ");
                // filter duplicates out of the result because options -c and -m may return
                // same files at times
                return (await RunGitCmdAsync("ls-files -z -o -m -c -i " + excludeParams).ConfigureAwait(false))
                    .Split(new[] { '\0', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Distinct()
                    .ToList();
            }
            else
            {
                return new string[] { };
            }
        }

        public async Task<string[]> GetFullTreeAsync(string id)
        {
            string tree = await this.RunCacheableCmdAsync(AppSettings.GitCommand, String.Format("ls-tree -z -r --name-only {0}", id), SystemEncoding).ConfigureAwait(false);
            return tree.Split(new char[] { '\0', '\n' });
        }

        public async Task<IEnumerable<IGitItem>> GetTreeAsync(string id, bool full)
        {
            string args = "-z";
            if (full)
                args += " -r";

            string tree;

            if (GitRevision.IsFullSha1Hash(id))
            {
                tree = await this.RunCacheableCmdAsync(AppSettings.GitCommand, "ls-tree " + args + " \"" + id + "\"", SystemEncoding).ConfigureAwait(false);
            }
            else
            {
                tree = await this.RunCmdAsync(AppSettings.GitCommand, "ls-tree " + args + " \"" + id + "\"", SystemEncoding).ConfigureAwait(false);
            }

            return _gitTreeParser.Parse(tree);
        }

        public async Task<GitBlame> BlameAsync(string filename, string from, Encoding encoding)
        {
            return await BlameAsync(filename, from, null, encoding).ConfigureAwait(false);
        }

        public async Task<GitBlame> BlameAsync(string filename, string from, string lines, Encoding encoding)
        {
            from = from.ToPosixPath();
            filename = filename.ToPosixPath();

            string detectCopyInFileOpt = AppSettings.DetectCopyInFileOnBlame ? " -M" : string.Empty;
            string detectCopyInAllOpt = AppSettings.DetectCopyInAllOnBlame ? " -C" : string.Empty;
            string ignoreWhitespaceOpt = AppSettings.IgnoreWhitespaceOnBlame ? " -w" : string.Empty;
            string linesOpt = lines != null ? " -L " + lines : string.Empty;

            string blameCommand = $"blame --porcelain{detectCopyInFileOpt}{detectCopyInAllOpt}{ignoreWhitespaceOpt} -l{linesOpt} \"{from}\" -- \"{filename}\"";
            var itemsStrings =
                (await RunCacheableCmdAsync(
                    AppSettings.GitCommand,
                    blameCommand,
                    LosslessEncoding
                    ).ConfigureAwait(false))
                    .Split('\n');

            GitBlame blame = new GitBlame();

            GitBlameHeader blameHeader = null;
            GitBlameLine blameLine = null;

            for (int i = 0; i < itemsStrings.GetLength(0); i++)
            {
                try
                {
                    string line = itemsStrings[i];

                    //The contents of the actual line is output after the above header, prefixed by a TAB. This is to allow adding more header elements later.
                    if (line.StartsWith("\t"))
                    {
                        blameLine.LineText = line.Substring(1) //trim ONLY first tab
                                                 .Trim(new char[] { '\r' }); //trim \r, this is a workaround for a \r\n bug
                        blameLine.LineText = ReEncodeStringFromLossless(blameLine.LineText, encoding);
                    }
                    else if (line.StartsWith("author-mail"))
                        blameHeader.AuthorMail = ReEncodeStringFromLossless(line.Substring("author-mail".Length).Trim());
                    else if (line.StartsWith("author-time"))
                        blameHeader.AuthorTime = DateTimeUtils.ParseUnixTime(line.Substring("author-time".Length).Trim());
                    else if (line.StartsWith("author-tz"))
                        blameHeader.AuthorTimeZone = line.Substring("author-tz".Length).Trim();
                    else if (line.StartsWith("author"))
                    {
                        blameHeader = new GitBlameHeader();
                        blameHeader.CommitGuid = blameLine.CommitGuid;
                        blameHeader.Author = ReEncodeStringFromLossless(line.Substring("author".Length).Trim());
                        blame.Headers.Add(blameHeader);
                    }
                    else if (line.StartsWith("committer-mail"))
                        blameHeader.CommitterMail = line.Substring("committer-mail".Length).Trim();
                    else if (line.StartsWith("committer-time"))
                        blameHeader.CommitterTime = DateTimeUtils.ParseUnixTime(line.Substring("committer-time".Length).Trim());
                    else if (line.StartsWith("committer-tz"))
                        blameHeader.CommitterTimeZone = line.Substring("committer-tz".Length).Trim();
                    else if (line.StartsWith("committer"))
                        blameHeader.Committer = ReEncodeStringFromLossless(line.Substring("committer".Length).Trim());
                    else if (line.StartsWith("summary"))
                        blameHeader.Summary = ReEncodeStringFromLossless(line.Substring("summary".Length).Trim());
                    else if (line.StartsWith("filename"))
                        blameHeader.FileName = ReEncodeFileNameFromLossless(line.Substring("filename".Length).Trim());
                    else if (line.IndexOf(' ') == 40) //SHA1, create new line!
                    {
                        blameLine = new GitBlameLine();
                        var headerParams = line.Split(' ');
                        blameLine.CommitGuid = headerParams[0];
                        if (headerParams.Length >= 3)
                        {
                            blameLine.OriginLineNumber = int.Parse(headerParams[1]);
                            blameLine.FinalLineNumber = int.Parse(headerParams[2]);
                        }
                        blame.Lines.Add(blameLine);
                    }
                }
                catch
                {
                    //Catch all parser errors, and ignore them all!
                    //We should never get here...
                    AppSettings.GitLog.Log("Error parsing output from command: " + blameCommand + "\n\nPlease report a bug!", DateTime.Now, DateTime.Now);
                }
            }

            return blame;
        }

        public async Task<string> GetFileTextAsync(string id, Encoding encoding)
        {
            return await RunCacheableCmdAsync(AppSettings.GitCommand, "cat-file blob \"" + id + "\"", encoding).ConfigureAwait(false);
        }

        public async Task<string> GetFileBlobHashAsync(string fileName, string revision)
        {
            if (revision == GitRevision.UnstagedGuid) //working directory changes
            {
                Debug.Assert(false, "Tried to get blob for unstaged file");
                return null;
            }
            if (revision == GitRevision.IndexGuid) //index
            {
                string blob = await RunGitCmdAsync(string.Format("ls-files -s \"{0}\"", fileName)).ConfigureAwait(false);
                string[] s = blob.Split(new char[] { ' ', '\t' });
                if (s.Length >= 2)
                    return s[1];

            }
            else
            {
                string blob = await RunGitCmdAsync(string.Format("ls-tree -r {0} \"{1}\"", revision, fileName)).ConfigureAwait(false);
                string[] s = blob.Split(new char[] { ' ', '\t' });
                if (s.Length >= 3)
                    return s[2];
            }
            return string.Empty;
        }

        public static void StreamCopy(Stream input, Stream output)
        {
            int read;
            var buffer = new byte[2048];
            do
            {
                read = input.Read(buffer, 0, buffer.Length);
                output.Write(buffer, 0, read);
            } while (read > 0);
        }

        public Stream GetFileStream(string blob)
        {
            try
            {
                var newStream = new MemoryStream();

                using (var process = RunGitCmdDetached("cat-file blob " + blob))
                {
                    StreamCopy(process.StandardOutput.BaseStream, newStream);
                    newStream.Position = 0;

                    process.WaitForExit();
                    return newStream;
                }
            }
            catch (Win32Exception ex)
            {
                Trace.WriteLine(ex);
            }

            return null;
        }

        public async Task<IEnumerable<string>> GetPreviousCommitMessagesAsync(int count)
        {
            return await GetPreviousCommitMessagesAsync("HEAD", count).ConfigureAwait(false);
        }

        public async Task<IEnumerable<string>> GetPreviousCommitMessagesAsync(string revision, int count)
        {
            string sep = "d3fb081b9000598e658da93657bf822cc87b2bf6";
            string output = await RunGitCmdAsync("log -n " + count + " " + revision + " --pretty=format:" + sep + "%e%n%s%n%n%b", LosslessEncoding).ConfigureAwait(false);
            string[] messages = output.Split(new string[] { sep }, StringSplitOptions.RemoveEmptyEntries);

            if (messages.Length == 0)
                return new string[] { string.Empty };

            return messages.Select(cm =>
                {
                    int idx = cm.IndexOf("\n");
                    string encodingName = cm.Substring(0, idx);
                    cm = cm.Substring(idx + 1, cm.Length - idx - 1);
                    cm = ReEncodeCommitMessage(cm, encodingName);
                    return cm;

                });
        }

        public string OpenWithDifftool(string filename, string oldFileName = "", string firstRevision = GitRevision.IndexGuid, string secondRevision = GitRevision.UnstagedGuid, string extraDiffArguments = "", bool isTracked = true)
        {
            var output = "";

            string args = string.Join(" ", extraDiffArguments, _revisionDiffProvider.Get(firstRevision, secondRevision, filename, oldFileName, isTracked));
            RunGitCmdDetached("difftool --gui --no-prompt " + args);
            return output;
        }

        public async Task<string> RevParseAsync(string revisionExpression)
        {
            string revparseCommand = string.Format("rev-parse \"{0}~0\"", revisionExpression);
            var result = await RunGitCmdResultAsync(revparseCommand).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.StdOutput.Split('\n')[0] : "";
        }

        public async Task<string> GetMergeBaseAsync(string a, string b)
        {
            return (await RunGitCmdAsync("merge-base " + a + " " + b).ConfigureAwait(false)).TrimEnd();
        }

        public async Task<SubmoduleStatus> CheckSubmoduleStatusAsync(string commit, string oldCommit, CommitData data, CommitData olddata, bool loaddata = false)
        {
            if (!IsValidGitWorkingDir() || oldCommit == null)
                return SubmoduleStatus.NewSubmodule;

            if (commit == null || commit == oldCommit)
                return SubmoduleStatus.Unknown;

            string baseCommit = await GetMergeBaseAsync(commit, oldCommit).ConfigureAwait(false);
            if (baseCommit == oldCommit)
                return SubmoduleStatus.FastForward;
            else if (baseCommit == commit)
                return SubmoduleStatus.Rewind;

            string error = "";
            if (loaddata)
                (olddata, error) = await _commitDataManager.GetCommitDataAsync(oldCommit).ConfigureAwait(false);
            if (olddata == null)
                return SubmoduleStatus.NewSubmodule;
            if (loaddata)
                (data, error) = await _commitDataManager.GetCommitDataAsync(commit).ConfigureAwait(false);
            if (data == null)
                return SubmoduleStatus.Unknown;
            if (data.CommitDate > olddata.CommitDate)
                return SubmoduleStatus.NewerTime;
            else if (data.CommitDate < olddata.CommitDate)
                return SubmoduleStatus.OlderTime;
            else if (data.CommitDate == olddata.CommitDate)
                return SubmoduleStatus.SameTime;
            return SubmoduleStatus.Unknown;
        }

        public async Task<SubmoduleStatus> CheckSubmoduleStatusAsync(string commit, string oldCommit)
        {
            return await CheckSubmoduleStatusAsync(commit, oldCommit, null, null, true).ConfigureAwait(false);
        }

        /// <summary>
        /// Uses check-ref-format to ensure that a branch name is well formed.
        /// </summary>
        /// <param name="branchName">Branch name to test.</param>
        /// <returns>true if <see cref="branchName"/> is valid reference name, otherwise false.</returns>
        public async Task<bool> CheckBranchFormatAsync([NotNull] string branchName)
        {
            if (branchName == null)
                throw new ArgumentNullException("branchName");

            if (branchName.IsNullOrWhiteSpace())
                return false;

            branchName = branchName.Replace("\"", "\\\"");

            var result = await RunGitCmdResultAsync(string.Format("check-ref-format --branch \"{0}\"", branchName)).ConfigureAwait(false);
            return result.ExitCode == 0;
        }

        /// <summary>
        /// Format branch name, check if name is valid for repository.
        /// </summary>
        /// <param name="branchName">Branch name to test.</param>
        /// <returns>Well formed branch name.</returns>
        public async Task<string> FormatBranchNameAsync([NotNull] string branchName)
        {
            if (branchName == null)
                throw new ArgumentNullException("branchName");

            string fullBranchName = GitCommandHelpers.GetFullBranchName(branchName);
            if (String.IsNullOrEmpty(await RevParseAsync(fullBranchName).ConfigureAwait(false)))
                fullBranchName = branchName;

            return fullBranchName;
        }

        public async Task<bool> IsRunningGitProcessAsync()
        {
            if (_indexLockManager.IsIndexLocked())
            {
                return true;
            }

            if (EnvUtils.RunningOnWindows())
            {
                return Process.GetProcessesByName("git").Length > 0;
            }

            // Get processes by "ps" command.
            var cmd = Path.Combine(AppSettings.GitBinDir, "ps");
            var arguments = "x";
            var output = await RunCmdAsync(cmd, arguments).ConfigureAwait(false);
            var lines = output.Split('\n');
            if (lines.Count() >= 2)
                return false;
            var headers = lines[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var commandIndex = Array.IndexOf(headers, "COMMAND");
            for (int i = 1; i < lines.Count(); i++)
            {
                var columns = lines[i].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (commandIndex < columns.Count())
                {
                    var command = columns[commandIndex];
                    if (command.EndsWith("/git"))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static string UnquoteFileName(string fileName)
        {
            if (fileName.IsNullOrWhiteSpace())
            {
                return fileName;
            }

            char[] chars = fileName.ToCharArray();
            IList<byte> blist = new List<byte>();
            int i = 0;
            StringBuilder sb = new StringBuilder();
            while (i < chars.Length)
            {
                char c = chars[i];
                if (c == '\\')
                {
                    //there should be 3 digits
                    if (chars.Length >= i + 3)
                    {
                        string octNumber = "" + chars[i + 1] + chars[i + 2] + chars[i + 3];

                        try
                        {
                            int code = Convert.ToInt32(octNumber, 8);
                            blist.Add((byte)code);
                            i += 4;
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                else
                {
                    if (blist.Count > 0)
                    {
                        sb.Append(SystemEncoding.GetString(blist.ToArray()));
                        blist.Clear();
                    }

                    sb.Append(c);
                    i++;
                }
            }
            if (blist.Count > 0)
            {
                sb.Append(SystemEncoding.GetString(blist.ToArray()));
                blist.Clear();
            }
            return sb.ToString();
        }

        public static string ReEncodeFileNameFromLossless(string fileName)
        {
            fileName = ReEncodeStringFromLossless(fileName, SystemEncoding);
            return UnquoteFileName(fileName);
        }

        public static string ReEncodeString(string s, Encoding fromEncoding, Encoding toEncoding)
        {
            if (s == null || fromEncoding.HeaderName.Equals(toEncoding.HeaderName))
                return s;
            else
            {
                byte[] bytes = fromEncoding.GetBytes(s);
                s = toEncoding.GetString(bytes);
                return s;
            }
        }

        /// <summary>
        /// reencodes string from GitCommandHelpers.LosslessEncoding to toEncoding
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        public static string ReEncodeStringFromLossless(string s, Encoding toEncoding)
        {
            if (toEncoding == null)
                return s;
            return ReEncodeString(s, LosslessEncoding, toEncoding);
        }

        public string ReEncodeStringFromLossless(string s)
        {
            return ReEncodeStringFromLossless(s, LogOutputEncoding);
        }

        //there was a bug: Git before v1.8.4 did not recode commit message when format is given
        //Lossless encoding is used, because LogOutputEncoding might not be lossless and not recoded
        //characters could be replaced by replacement character while reencoding to LogOutputEncoding
        public string ReEncodeCommitMessage(string s, string toEncodingName)
        {

            bool isABug = !GitCommandHelpers.VersionInUse.LogFormatRecodesCommitMessage;

            Encoding encoding;
            try
            {
                if (isABug)
                {
                    if (toEncodingName.IsNullOrEmpty())
                        encoding = Encoding.UTF8;
                    else if (toEncodingName.Equals(LosslessEncoding.HeaderName, StringComparison.InvariantCultureIgnoreCase))
                        encoding = null; //no recoding is needed
                    else if (CpEncodingPattern.IsMatch(toEncodingName)) // Encodings written as e.g. "cp1251", which is not a supported encoding string
                        encoding = Encoding.GetEncoding(int.Parse(toEncodingName.Substring(2)));
                    else
                        encoding = Encoding.GetEncoding(toEncodingName);
                }
                else//bug is fixed in Git v1.8.4, Git recodes commit message to LogOutputEncoding
                    encoding = LogOutputEncoding;

            }
            catch (Exception)
            {
                return s + "\n\n! Unsupported commit message encoding: " + toEncodingName + " !";
            }
            return ReEncodeStringFromLossless(s, encoding);
        }

        /// <summary>
        /// header part of show result is encoded in logoutputencoding (including reencoded commit message)
        /// diff part is raw data in file's original encoding
        /// s should be encoded in LosslessEncoding
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        public string ReEncodeShowString(string s)
        {
            if (s.IsNullOrEmpty())
                return s;

            int p = s.IndexOf("diff --git");
            string header;
            string diffHeader;
            string diffContent;
            string diff;
            if (p > 0)
            {
                header = s.Substring(0, p);
                diff = s.Substring(p);
            }
            else
            {
                header = string.Empty;
                diff = s;
            }

            p = diff.IndexOf("@@");
            if (p > 0)
            {
                diffHeader = diff.Substring(0, p);
                diffContent = diff.Substring(p);
            }
            else
            {
                diffHeader = string.Empty;
                diffContent = diff;
            }

            header = ReEncodeString(header, LosslessEncoding, LogOutputEncoding);
            diffHeader = ReEncodeFileNameFromLossless(diffHeader);
            diffContent = ReEncodeString(diffContent, LosslessEncoding, FilesEncoding);
            return header + diffHeader + diffContent;
        }

        public override bool Equals(object obj)
        {
            if (obj == null) { return false; }
            if (obj == this) { return true; }

            GitModule other = obj as GitModule;
            return (other != null) && Equals(other);
        }

        bool Equals(GitModule other)
        {
            return
                string.Equals(_workingDir, other._workingDir) &&
                Equals(_superprojectModule, other._superprojectModule);
        }

        public override int GetHashCode()
        {
            return _workingDir.GetHashCode();
        }

        public override string ToString()
        {
            return WorkingDir;
        }

        public string GetLocalTrackingBranchName(string remoteName, string branch)
        {
            var branchName = remoteName.Length > 0 ? branch.Substring(remoteName.Length + 1) : branch;
            foreach (var section in LocalConfigFile.GetConfigSections())
            {
                if (section.SectionName == "branch" && section.GetValue("remote") == remoteName)
                {
                    var remoteBranch = section.GetValue("merge").Replace("refs/heads/", string.Empty);
                    if (remoteBranch == branchName)
                    {
                        return section.SubSection;
                    }
                }
            }
            return branchName;
        }

        public async Task<IList<GitItemStatus>> GetCombinedDiffFileListAsync(string shaOfMergeCommit)
        {
            var fileList = await RunGitCmdAsync("diff-tree --name-only -z --cc --no-commit-id " + shaOfMergeCommit).ConfigureAwait(false);

            var ret = new List<GitItemStatus>();
            if (string.IsNullOrWhiteSpace(fileList))
            {
                return ret;
            }

            var files = fileList.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var file in files)
            {
                var item = new GitItemStatus
                {
                    IsChanged = true,
                    IsConflict = true,
                    IsTracked = true,
                    IsDeleted = false,
                    IsStaged = false,
                    IsNew = false,
                    Name = file,
                };
                ret.Add(item);
            }

            return ret;
        }

        public async Task<string> GetCombinedDiffContentAsync(GitRevision revisionOfMergeCommit, string filePath,
            string extraArgs, Encoding encoding)
        {
            var cmd = string.Format("diff-tree {4} --no-commit-id {0} {1} {2} -- {3}",
                extraArgs,
                revisionOfMergeCommit.Guid,
                AppSettings.UsePatienceDiffAlgorithm ? "--patience" : "",
                filePath,
                AppSettings.OmitUninterestingDiff ? "--cc" : "-c -p");

            var patchManager = new PatchManager();
            var patch = await RunCacheableCmdAsync(AppSettings.GitCommand, cmd, LosslessEncoding).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(patch))
            {
                return "";
            }

            patchManager.LoadPatch(patch, false, encoding);
            return GetPatch(patchManager, filePath, filePath).Text;
        }

        public async Task<bool> HasLfsSupportAsync()
        {
            return (await RunGitCmdResultAsync("lfs version").ConfigureAwait(false)).ExitedSuccessfully;
        }

        public async Task<bool> StopTrackingFileAsync(string filename)
        {
            return (await RunGitCmdResultAsync("rm --cached " + filename).ConfigureAwait(false)).ExitedSuccessfully;
        }
    }
}
