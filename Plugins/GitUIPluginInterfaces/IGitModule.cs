using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace GitUIPluginInterfaces
{
    /// <summary>Provides manipulation with git module.</summary>
    public interface IGitModule
    {
        IConfigFileSettings LocalConfigFile { get; }

        Task<string> AddRemoteAsync(string remoteName, string path);
        Task<IList<IGitRef>> GetRefsAsync(bool tags = true, bool branches = true);
        IEnumerable<string> GetSettings(string setting);
        Task<IEnumerable<IGitItem>> GetTreeAsync(string id, bool full);

        /// <summary>
        /// Removes the registered remote by running <c>git remote rm</c> command.
        /// </summary>
        /// <param name="remoteName">The remote name.</param>
        Task<string> RemoveRemoteAsync(string remoteName);

        /// <summary>
        /// Renames the registered remote by running <c>git remote rename</c> command.
        /// </summary>
        /// <param name="remoteName">The current remote name.</param>
        /// <param name="newName">The new remote name.</param>
        Task<string> RenameRemoteAsync(string remoteName, string newName);
        void SetSetting(string setting, string value);
        void UnsetSetting(string setting);

        /// <summary>
        /// Run git command, console window is hidden, redirect output
        /// </summary>
        Process RunGitCmdDetached(string arguments, Encoding encoding = null);

        /// <summary>
        /// Run git command, console window is hidden, wait for exit, redirect output
        /// </summary>
        Task<string> RunGitCmdAsync(string arguments, Encoding encoding = null, byte[] stdInput = null);

        /// <summary>
        /// Run git command, console window is hidden, wait for exit, redirect output
        /// </summary>
        Task<CmdResult> RunGitCmdResultAsync(string arguments, Encoding encoding = null, byte[] stdInput = null);

        /// <summary>
        /// Run command, console window is hidden, wait for exit, redirect output
        /// </summary>
        Task<string> RunCmdAsync(string cmd, string arguments, Encoding encoding = null, byte[] stdIn = null);

        /// <summary>
        /// Run command, console window is hidden, wait for exit, redirect output
        /// </summary>
        Task<CmdResult> RunCmdResultAsync(string cmd, string arguments, Encoding encoding = null, byte[] stdInput = null);

        Task<string> RunBatchFileAsync(string batchFile);

        /// <summary>
        /// Determines whether the given repository has index.lock file.
        /// </summary>
        /// <returns><see langword="true"/> is index is locked; otherwise <see langword="false"/>.</returns>
        bool IsIndexLocked();

        /// <summary>
        /// Delete index.lock in the current working folder.
        /// </summary>
        /// <param name="includeSubmodules">
        ///     If <see langword="true"/> all submodules will be scanned for index.lock files and have them delete, if found.
        /// </param>
        void UnlockIndex(bool includeSubmodules);

        /// <summary>Gets the directory which contains the git repository.</summary>
        string WorkingDir { get; }

        /// <summary>
        /// Gets the location of .git directory for the current working folder.
        /// </summary>
        string WorkingDirGitDir { get; }

        /// <summary>
        /// Asks git to resolve the given relativePath
        /// git special folders are located in different directories depending on the kind of repo: submodule, worktree, main
        /// See https://git-scm.com/docs/git-rev-parse#git-rev-parse---git-pathltpathgt
        /// </summary>
        /// <param name="relativePath">A path relative to the .git directory</param>
        /// <returns></returns>
        Task<string> ResolveGitInternalPathAsync(string relativePath);

        /// <summary>Indicates whether the specified directory contains a git repository.</summary>
        bool IsValidGitWorkingDir();

        /// <summary>Indicates whether the repository is in a 'detached HEAD' state.</summary>
        Task<bool> IsDetachedHeadAsync();

        Task<(bool, string fullSha1)> IsExistingCommitHashAsync(string sha1Fragment);

        /// <summary>Gets the path to the git application executable.</summary>
        string GitCommand { get; }

        Version AppVersion { get; }

        string GravatarCacheDir { get; }

        string GetSubmoduleFullPath(string localPath);

        IEnumerable<IGitSubmoduleInfo> GetSubmodulesInfo();

        IList<string> GetSubmodulesLocalPaths(bool recursive = true);

        IGitModule GetSubmodule(string submoduleName);

        /// <summary>
        /// Retrieves registered remotes by running <c>git remote show</c> command.
        /// </summary>
        /// <returns>Registered remotes.</returns>
        Task<string[]> GetRemotesAsync();

        /// <summary>
        /// Retrieves registered remotes by running <c>git remote show</c> command.
        /// </summary>
        /// <param name="allowEmpty"></param>
        /// <returns>Registered remotes.</returns>
        Task<string[]> GetRemotesAsync(bool allowEmpty);

        string GetSetting(string setting);
        string GetEffectiveSetting(string setting);

        Task<bool> StartPageantForRemoteAsync(string remote);

        /// <summary>Gets the current branch; or "(no branch)" if HEAD is detached.</summary>
        Task<string> GetSelectedBranchAsync();

        /// <summary>true if ".git" directory does NOT exist.</summary>
        bool IsBareRepository();

        Task<bool> IsRunningGitProcessAsync();

        ISettingsSource GetEffectiveSettings();

        string ReEncodeStringFromLossless(string s);

        string ReEncodeCommitMessage(string s, string toEncodingName);
    }
}
