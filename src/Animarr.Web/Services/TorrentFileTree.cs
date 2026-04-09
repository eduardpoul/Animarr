namespace Animarr.Web.Services;

public record FileTreeNode(string Name, string? FullPath, long Length, List<FileTreeNode> Children)
{
    public bool IsFile => FullPath is not null;
}

public static class TorrentFileTree
{
    public static FileTreeNode BuildTree(IEnumerable<TorrentFileEntry> files)
    {
        var root = new FileTreeNode("root", null, 0, []);
        foreach (var file in files)
        {
            var parts = file.Path.Replace('\\', '/').Split('/');
            var node = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var existing = node.Children.FirstOrDefault(c => c.Name == parts[i] && !c.IsFile);
                if (existing is null)
                {
                    existing = new FileTreeNode(parts[i], null, 0, []);
                    node.Children.Add(existing);
                }
                node = existing;
            }
            node.Children.Add(new FileTreeNode(parts[^1], string.Join("/", parts), file.Length, []));
        }
        return root;
    }

    /// <summary>Flattens the tree into a list of (isFolder, name, fullPath, length, depth).</summary>
    public static List<(bool IsFolder, string Name, string? FullPath, long Length, int Depth)> Flatten(FileTreeNode root)
    {
        var result = new List<(bool, string, string?, long, int)>();
        FlattenNode(root, 0, "", result);
        return result;
    }

    private static void FlattenNode(FileTreeNode node, int depth, string parentPath, List<(bool, string, string?, long, int)> list)
    {
        foreach (var child in node.Children)
        {
            var childPath = parentPath == "" ? child.Name : $"{parentPath}/{child.Name}";
            if (!child.IsFile)
            {
                list.Add((true, child.Name, childPath, 0, depth));
                FlattenNode(child, depth + 1, childPath, list);
            }
            else
            {
                list.Add((false, child.Name, child.FullPath, child.Length, depth));
            }
        }
    }
}
