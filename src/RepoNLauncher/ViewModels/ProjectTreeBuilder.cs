using System.IO;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// Arranges the flat project list into collapsible folder sections. Every scanned folder is a top-level section;
/// inside it, a subfolder holding two or more repos (at any depth) gets its own nested section, while a subfolder
/// with a single repo just lists that repo in the parent section. Projects outside every scanned folder stay at
/// the top level. Each level lists its repos first, then its subsections.
/// </summary>
public static class ProjectTreeBuilder
{
    private const char Separator = '\\';

    public static List<object> Build(
        IEnumerable<ProjectViewModel> projects,
        IEnumerable<string> scanFolders,
        Func<string, bool> isExpanded,
        Action<ProjectGroupViewModel> expandedChanged)
    {
        var roots = scanFolders.Select(Normalize).Where(r => r.Length > 0).ToList();
        var rootNodes = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        var loose = new List<ProjectViewModel>();

        foreach (var project in projects)
        {
            var path = Normalize(project.Path);
            // The outermost scanned folder wins, so a folder that was also scanned on its own still shows up nested in its parent.
            var root = roots.Where(r => IsSameOrUnder(path, r)).MinBy(r => r.Length);
            if (root == null)
            {
                loose.Add(project);
                continue;
            }

            if (!rootNodes.TryGetValue(root, out var node))
                rootNodes[root] = node = new FolderNode(FolderName(root), root);

            // Each folder between the scanned folder and the repo's own folder is a potential section.
            var segments = path[root.Length..].Split(Separator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments.SkipLast(1))
                node = node.GetOrAddChild(segment);
            node.Repos.Add(project);
        }

        var items = new List<object>(SortByName(loose));
        items.AddRange(rootNodes.Values
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => ToGroup(n, isExpanded, expandedChanged)));
        return items;
    }

    private static ProjectGroupViewModel ToGroup(FolderNode node, Func<string, bool> isExpanded, Action<ProjectGroupViewModel> expandedChanged)
    {
        var name = node.Name;
        var (repos, sections) = Split(node);
        // A folder that only leads to one subsection adds a level without adding anything; show the two as one "outer / inner" section.
        while (repos.Count == 0 && sections.Count == 1)
        {
            node = sections[0];
            name = $"{name} / {node.Name}";
            (repos, sections) = Split(node);
        }

        var group = new ProjectGroupViewModel(name, node.Path, node.AllRepos().ToList(), isExpanded(node.Path), expandedChanged);
        foreach (var repo in SortByName(repos))
            group.Children.Add(repo);
        foreach (var section in sections.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            group.Children.Add(ToGroup(section, isExpanded, expandedChanged));
        return group;
    }

    // A folder's contents as its section shows them: its own repos plus those of single-repo subfolders,
    // and the subfolders with enough repos to be sections of their own.
    private static (List<ProjectViewModel> Repos, List<FolderNode> Sections) Split(FolderNode node)
    {
        var repos = new List<ProjectViewModel>(node.Repos);
        var sections = new List<FolderNode>();
        foreach (var child in node.Children.Values)
        {
            if (child.RepoCount >= 2) sections.Add(child);
            else repos.AddRange(child.AllRepos());
        }
        return (repos, sections);
    }

    private static IEnumerable<ProjectViewModel> SortByName(IEnumerable<ProjectViewModel> projects) =>
        projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Trim().Replace('/', Separator).TrimEnd(Separator);

    private static bool IsSameOrUnder(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        (path.Length > folder.Length && path[folder.Length] == Separator &&
         path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));

    private static string FolderName(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 0 ? name : path; // a drive root like "D:" has no folder name
    }

    private sealed class FolderNode
    {
        public FolderNode(string name, string path)
        {
            Name = name;
            Path = path;
        }

        public string Name { get; }
        public string Path { get; }
        public List<ProjectViewModel> Repos { get; } = new();
        public Dictionary<string, FolderNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int RepoCount => Repos.Count + Children.Values.Sum(c => c.RepoCount);

        public IEnumerable<ProjectViewModel> AllRepos() => Repos.Concat(Children.Values.SelectMany(c => c.AllRepos()));

        public FolderNode GetOrAddChild(string name)
        {
            if (!Children.TryGetValue(name, out var child))
                Children[name] = child = new FolderNode(name, Path + Separator + name);
            return child;
        }
    }
}
