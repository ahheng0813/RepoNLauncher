using System.IO;

namespace RepoNLauncher.ViewModels;

/// <summary>
/// Arranges the flat project list into collapsible folder sections. Every scanned folder is a top-level section;
/// inside it, a subfolder holding two or more repos (at any depth, the subfolder itself counting when it's a repo)
/// gets its own nested section, while a subfolder with a single repo just lists that repo in the parent section.
/// Projects outside every scanned folder that share a parent folder with another such project get a top-level section
/// for that folder too, built the same way; the rest stay at the top level. Each level lists its repos first, the
/// folder's own repo leading, then its subsections.
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
        var all = projects.Select(p => (Project: p, Path: Normalize(p.Path))).ToList();
        var scanRoots = scanFolders.Select(Normalize).Where(r => r.Length > 0).ToList();
        var outsideScans = all.Where(p => !scanRoots.Any(r => IsSameOrUnder(p.Path, r))).ToList();
        var roots = scanRoots.Concat(SharedParents(outsideScans.Select(p => p.Path))).ToList();

        var rootNodes = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        var loose = new List<ProjectViewModel>();

        foreach (var (project, path) in all)
        {
            // The outermost folder wins, so a folder that was also scanned on its own still shows up nested in its parent.
            var root = roots.Where(r => IsSameOrUnder(path, r)).MinBy(r => r.Length);
            if (root == null)
            {
                loose.Add(project);
                continue;
            }

            if (!rootNodes.TryGetValue(root, out var node))
                rootNodes[root] = node = new FolderNode(FolderName(root), root);

            // Each folder below the scanned folder down to the repo's own is a potential section; the repo's own one
            // becomes a section when the repo holds repos of its own.
            var segments = path[root.Length..].Split(Separator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
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
        foreach (var repo in repos)
            group.Children.Add(repo);
        foreach (var section in sections.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            group.Children.Add(ToGroup(section, isExpanded, expandedChanged));
        return group;
    }

    // A folder's contents as its section shows them: the folder's own repo when it is one, then those of single-repo
    // subfolders by name, and the subfolders with enough repos to be sections of their own.
    private static (List<ProjectViewModel> Repos, List<FolderNode> Sections) Split(FolderNode node)
    {
        var inSubfolders = new List<ProjectViewModel>();
        var sections = new List<FolderNode>();
        foreach (var child in node.Children.Values)
        {
            if (child.RepoCount >= 2) sections.Add(child);
            else inSubfolders.AddRange(child.AllRepos());
        }
        return (SortByName(node.Repos).Concat(SortByName(inSubfolders)).ToList(), sections);
    }

    // The parent folders that directly hold two or more of the given repos, such as the folder repos were cloned into
    // when they were added one by one rather than by scanning it.
    private static IEnumerable<string> SharedParents(IEnumerable<string> paths) =>
        paths.Select(ParentFolder)
            .Where(parent => parent.Length > 0)
            .GroupBy(parent => parent, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key);

    private static string ParentFolder(string path) => Normalize(Path.GetDirectoryName(path) ?? "");

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
