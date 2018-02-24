using System;
using System.Threading.Tasks;
using GitUIPluginInterfaces;

namespace GitCommands.Git
{
    public interface IGitRevisionProvider
    {
        Task<GitRevision> GetAsync(string sha1);
    }

    public sealed class GitRevisionProvider : IGitRevisionProvider
    {
        private readonly Func<IGitModule> _getModule;


        public GitRevisionProvider(Func<IGitModule> getModule)
        {
            _getModule = getModule;
        }


        public async Task<GitRevision> GetAsync(string sha1)
        {
            if (sha1.IsNullOrWhiteSpace() || sha1.Length >= 40)
            {
                return new GitRevision(sha1);
            }

            var module = GetModule();
            var (success, fullSha1) = await module.IsExistingCommitHashAsync(sha1).ConfigureAwait(false);
            if (success)
            {
                sha1 = fullSha1;
            }

            return new GitRevision(sha1);
        }

        private IGitModule GetModule()
        {
            var module = _getModule();
            if (module == null)
            {
                throw new ArgumentException($"Require a valid instance of {nameof(IGitModule)}");
            }
            return module;
        }
    }
}