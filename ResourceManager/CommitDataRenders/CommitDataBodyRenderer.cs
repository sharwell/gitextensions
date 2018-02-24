using System;
using System.Net;
using System.Threading.Tasks;
using GitCommands;
using GitUI;
using GitUIPluginInterfaces;

namespace ResourceManager.CommitDataRenders
{
    /// <summary>
    /// Provides the ability to render the body of a commit message.
    /// </summary>
    public interface ICommitDataBodyRenderer 
    {
        /// <summary>
        /// Render the body of a commit message.
        /// </summary>
        string Render(CommitData commitData, bool showRevisionsAsLinks);
    }

    /// <summary>
    /// Renders the body of a commit message.
    /// </summary>
    public sealed class CommitDataBodyRenderer : ICommitDataBodyRenderer
    {
        private readonly Func<IGitModule> _getModule;
        private readonly ILinkFactory _linkFactory;


        public CommitDataBodyRenderer(Func<IGitModule> getModule, ILinkFactory linkFactory)
        {
            _getModule = getModule;
            _linkFactory = linkFactory;
        }


        /// <summary>
        /// Render the body of a commit message.
        /// </summary>
        public string Render(CommitData commitData, bool showRevisionsAsLinks)
        {
            if (commitData == null)
            {
                throw new ArgumentNullException(nameof(commitData));
            }

            var body = "\n" + WebUtility.HtmlEncode((commitData.Body ?? "").Trim());

            if (showRevisionsAsLinks)
            {
                body = GitRevision.Sha1HashShortRegex.Replace(body, match => ThreadHelper.JoinableTaskFactory.Run(() => ProcessHashCandidateAsync(match.Value)));
            }
            return body;
        }

        private async Task<string> ProcessHashCandidateAsync(string hash)
        {
            var module = _getModule();
            if (module == null)
            {
                return hash;
            }

            (bool isHash, string fullHash) = await module.IsExistingCommitHashAsync(hash).ConfigureAwait(false);
            if (!isHash)
            {
                return null;
            }

            return _linkFactory.CreateCommitLink(fullHash, hash, true);
        }

    }
}