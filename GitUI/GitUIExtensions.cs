﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GitCommands;
using GitUI.Editor;
using ResourceManager;

namespace GitUI
{
    public static class GitUIExtensions
    {
        public static void OpenWithDifftool(this RevisionGrid grid, IReadOnlyList<GitRevision> revisions, string fileName, string oldFileName, GitUI.RevisionDiffKind diffKind, bool isTracked)
        {
            // Note: Order in revisions is that first clicked is last in array

            string error = RevisionDiffInfoProvider.Get(revisions, diffKind,
                out var extraDiffArgs, out var firstRevision, out var secondRevision);

            if (!string.IsNullOrEmpty(error))
            {
                MessageBox.Show(grid, error);
            }
            else
            {
                string output = grid.Module.OpenWithDifftool(fileName, oldFileName, firstRevision, secondRevision, extraDiffArgs, isTracked);
                if (!string.IsNullOrEmpty(output))
                {
                    MessageBox.Show(grid, output);
                }
            }
        }

        private static PatchApply.Patch GetItemPatch(GitModule module, GitItemStatus file,
            string firstRevision, string secondRevision, string diffArgs, Encoding encoding)
        {
            // Files with tree guid should be presented with normal diff
            var isTracked = file.IsTracked || (file.TreeGuid.IsNotNullOrWhitespace() && secondRevision.IsNotNullOrWhitespace());
            return module.GetSingleDiff(firstRevision, secondRevision, file.Name, file.OldName,
                    diffArgs, encoding, true, isTracked);
        }

        public static string GetSelectedPatch(this FileViewer diffViewer, string firstRevision, string secondRevision, GitItemStatus file)
        {
            if (!file.IsTracked)
            {
                var fullPath = Path.Combine(diffViewer.Module.WorkingDir, file.Name);
                if (Directory.Exists(fullPath) && GitModule.IsValidGitWorkingDir(fullPath))
                {
                    // git-status does not detect details for untracked and git-diff --no-index will not give info
                    return LocalizationHelpers.GetSubmoduleText(diffViewer.Module, file.Name.TrimEnd('/'), "");
                }
            }

            if (file.IsSubmodule && file.GetSubmoduleStatusAsync() != null)
            {
                return LocalizationHelpers.ProcessSubmoduleStatus(diffViewer.Module, ThreadHelper.JoinableTaskFactory.Run(() => file.GetSubmoduleStatusAsync()));
            }

            PatchApply.Patch patch = GetItemPatch(diffViewer.Module, file, firstRevision, secondRevision,
                diffViewer.GetExtraDiffArguments(), diffViewer.Encoding);

            if (patch == null)
            {
                return string.Empty;
            }

            if (file.IsSubmodule)
            {
                return LocalizationHelpers.ProcessSubmodulePatch(diffViewer.Module, file.Name, patch);
            }

            return patch.Text;
        }

        public static Task ViewChangesAsync(this FileViewer diffViewer, IReadOnlyList<GitRevision> revisions, GitItemStatus file, string defaultText)
        {
            if (revisions.Count == 0)
            {
                return Task.CompletedTask;
            }

            var selectedRevision = revisions[0];
            string secondRevision = selectedRevision?.Guid;
            string firstRevision = revisions.Count >= 2 ? revisions[1].Guid : null;
            if (firstRevision == null && selectedRevision != null)
            {
                firstRevision = selectedRevision.FirstParentGuid;
            }

            return ViewChangesAsync(diffViewer, firstRevision, secondRevision, file, defaultText);
        }

        public static Task ViewChangesAsync(this FileViewer diffViewer, string firstRevision, string secondRevision, GitItemStatus file, string defaultText)
        {
            if (firstRevision == null)
            {
                // The previous commit does not exist, nothing to compare with
                if (file.TreeGuid.IsNullOrEmpty())
                {
                    if (secondRevision.IsNullOrWhiteSpace())
                    {
                        throw new ArgumentException(nameof(secondRevision));
                    }

                    return diffViewer.ViewGitItemRevisionAsync(file.Name, secondRevision);
                }
                else
                {
                    return diffViewer.ViewGitItemAsync(file.Name, file.TreeGuid);
                }
            }
            else
            {
                return diffViewer.ViewPatchAsync(() =>
                {
                    string selectedPatch = diffViewer.GetSelectedPatch(firstRevision, secondRevision, file);
                    return selectedPatch ?? defaultText;
                });
            }
        }

        public static void RemoveIfExists(this TabControl tabControl, TabPage page)
        {
            if (tabControl.TabPages.Contains(page))
            {
                tabControl.TabPages.Remove(page);
            }
        }

        public static void InsertIfNotExists(this TabControl tabControl, int index, TabPage page)
        {
            if (!tabControl.TabPages.Contains(page))
            {
                tabControl.TabPages.Insert(index, page);
            }
        }

        public static void Mask(this Control control)
        {
            if (control.FindMaskPanel() == null)
            {
                MaskPanel panel = new MaskPanel();
                control.Controls.Add(panel);
                panel.Dock = DockStyle.Fill;
                panel.BringToFront();
            }
        }

        public static void UnMask(this Control control)
        {
            MaskPanel panel = control.FindMaskPanel();
            if (panel != null)
            {
                control.Controls.Remove(panel);
                panel.Dispose();
            }
        }

        private static MaskPanel FindMaskPanel(this Control control)
        {
            foreach (var c in control.Controls)
            {
                if (c is MaskPanel)
                {
                    return c as MaskPanel;
                }
            }

            return null;
        }

        public class MaskPanel : PictureBox
        {
            public MaskPanel()
            {
                Image = Properties.Resources.loadingpanel;
                SizeMode = PictureBoxSizeMode.CenterImage;
                BackColor = SystemColors.AppWorkspace;
            }
        }

        public static IEnumerable<TreeNode> AllNodes(this TreeView tree)
        {
            return tree.Nodes.AllNodes();
        }

        public static IEnumerable<TreeNode> AllNodes(this TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                yield return node;

                foreach (TreeNode subNode in node.Nodes.AllNodes())
                {
                    yield return subNode;
                }
            }
        }

        public static void PostToUIThread(this Control control, Action action)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(
                async () =>
                {
                    if (ThreadHelper.JoinableTaskContext.IsOnMainThread)
                    {
                        await Task.Yield();
                    }

                    await control.SwitchToMainThreadAsync();
                    action();
                })
            .FileAndForget();
        }

        public static void PostToUIThread<T>(this Control control, Action<T> action, T state)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(
                async () =>
                {
                    if (ThreadHelper.JoinableTaskContext.IsOnMainThread)
                    {
                        await Task.Yield();
                    }

                    await control.SwitchToMainThreadAsync();
                    action(state);
                })
            .FileAndForget();
        }

        public static void SendToUIThread(this Control control, Action action)
        {
            ThreadHelper.JoinableTaskFactory.Run(
                async () =>
                {
                    try
                    {
                        if (ThreadHelper.JoinableTaskContext.IsOnMainThread)
                        {
                            await Task.Yield();
                        }

                        await control.SwitchToMainThreadAsync();
                        action();
                    }
                    catch (Exception e)
                    {
                        e.Data["StackTrace" + e.Data.Count] = e.StackTrace;
                        throw;
                    }
                });
        }

        public static Control FindFocusedControl(this ContainerControl container)
        {
            var control = container.ActiveControl;
            container = control as ContainerControl;

            if (container == null)
            {
                return control;
            }
            else
            {
                return container.FindFocusedControl();
            }
        }
    }
}
