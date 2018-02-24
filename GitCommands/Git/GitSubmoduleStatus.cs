using System.Threading.Tasks;

namespace GitCommands
{
    public class GitSubmoduleStatus
    {
        public GitSubmoduleStatus()
        {
            Status = SubmoduleStatus.Unknown;
        }

        public string Name { get; set; }
        public string OldName { get; set; }
        public bool   IsDirty { get; set; }
        public string Commit { get; set; }
        public string OldCommit { get; set; }
        public SubmoduleStatus Status { get; set; }
        public int? AddedCommits { get; set; }
        public int? RemovedCommits { get; set; }

        public GitModule GetSubmodule(GitModule module)
        {
            return module.GetSubmodule(Name);
        }

        public async Task CheckSubmoduleStatusAsync(GitModule submodule)
        {
            Status = SubmoduleStatus.NewSubmodule;
            if (submodule == null)
                return;

            Status = await submodule.CheckSubmoduleStatusAsync(Commit, OldCommit).ConfigureAwait(false);
        }

        public string AddedAndRemovedString()
        {
            if (RemovedCommits == null || AddedCommits == null ||
                (RemovedCommits == 0 && AddedCommits == 0))
                return "";
            return " (" +
                ((RemovedCommits == 0) ? "" : ("-" + RemovedCommits)) +
                ((AddedCommits == 0) ? "" : ("+" + AddedCommits)) +
                ")";
        }
    }

}
