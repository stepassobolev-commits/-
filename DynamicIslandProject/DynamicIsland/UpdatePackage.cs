using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace DynamicIsland;

/// <summary>Разбор перетащенных файлов и сопоставление их с путями в репозитории.</summary>
public static class UpdatePackage
{
    public sealed record DroppedFile(string Name, byte[] Content);

    private static readonly HashSet<string> JunkFolders = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".vs", ".git", "dist", "publish", "__MACOSX" };

    private static readonly string[] SourceExtensions =
        { ".cs", ".xaml", ".csproj", ".manifest", ".ico", ".png", ".json", ".iss", ".ps1" };

    /// <summary>Читает файлы, папки и ZIP-архивы, которые перетащил пользователь.</summary>
    public static List<DroppedFile> Read(IEnumerable<string> paths)
    {
        var result = new List<DroppedFile>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var parent = Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? "";
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(parent, file);
                    AddIfUseful(result, relative, File.ReadAllBytes(file));
                }
            }
            else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = ZipFile.OpenRead(path);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // папка
                    using var stream = entry.Open();
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    AddIfUseful(result, entry.FullName, memory.ToArray());
                }
            }
            else if (File.Exists(path))
            {
                AddIfUseful(result, Path.GetFileName(path), File.ReadAllBytes(path));
            }
        }
        return result;
    }

    private static void AddIfUseful(List<DroppedFile> list, string name, byte[] content)
    {
        var normalized = name.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return;
        if (segments.Take(segments.Length - 1).Any(s => JunkFolders.Contains(s))) return;

        var fileName = FixBrowserName(segments[^1]);
        var lower = fileName.ToLowerInvariant();
        if (lower.EndsWith(".zip") || lower.EndsWith(".pdf") || lower.EndsWith(".bat") ||
            lower.StartsWith("readme") || lower is "thumbs.db" or "desktop.ini") return;

        segments[^1] = fileName;
        list.Add(new DroppedFile(string.Join('/', segments), content));
    }

    /// <summary>Браузеры добавляют « (1)» к имени и иногда меняют .xaml.cs на .pdf — возвращаем нормальное имя.</summary>
    public static string FixBrowserName(string fileName)
    {
        var name = Regex.Replace(fileName, @" \(\d+\)(?=\.[^.]+$)", "");
        if (Regex.IsMatch(name, @"_xaml\.pdf$", RegexOptions.IgnoreCase))
            name = Regex.Replace(name, @"_xaml\.pdf$", ".xaml.cs", RegexOptions.IgnoreCase);
        return name;
    }

    /// <summary>Определяет, в какой путь репозитория записать каждый файл.</summary>
    public static List<UpdateFile> MapToRepository(IEnumerable<DroppedFile> files, IReadOnlyList<string> tree,
        out List<string> skipped)
    {
        skipped = new List<string>();
        var csproj = tree.FirstOrDefault(p =>
            p.Equals("DynamicIsland.csproj", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith("/DynamicIsland.csproj", StringComparison.OrdinalIgnoreCase));
        if (csproj is null)
            throw new InvalidOperationException(
                "В репозитории не найден файл DynamicIsland.csproj. Проверьте адрес репозитория в настройках обновления.");

        var projectDir = ParentOf(csproj);
        var projectRoot = ParentOf(projectDir);
        var mapped = new Dictionary<string, UpdateFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var segments = file.Name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var repoPath = MapSegments(segments, tree, projectDir, projectRoot);
            if (repoPath is null)
            {
                skipped.Add(file.Name);
                continue;
            }
            mapped[repoPath] = new UpdateFile(repoPath, file.Content, file.Name);
        }
        return mapped.Values.ToList();
    }

    private static string? MapSegments(string[] segments, IReadOnlyList<string> tree, string projectDir, string projectRoot)
    {
        var last = segments[^1];

        // Внутри архива есть папка DynamicIsland — это папка с исходным кодом приложения.
        var index = Array.FindIndex(segments, 0, segments.Length - 1,
            s => s.Equals("DynamicIsland", StringComparison.OrdinalIgnoreCase));
        if (index >= 0) return Join(projectDir, segments[(index + 1)..]);

        // Папки установщика и скриптов лежат рядом с папкой приложения.
        index = Array.FindIndex(segments, 0, segments.Length - 1,
            s => s.Equals("Installer", StringComparison.OrdinalIgnoreCase) ||
                 s.Equals("Scripts", StringComparison.OrdinalIgnoreCase));
        if (index >= 0) return Join(projectRoot, segments[index..]);

        // Файлы сборки GitHub не трогаем: для них нужны отдельные права токена.
        if (segments.Any(s => s.Equals(".github", StringComparison.OrdinalIgnoreCase))) return null;

        // Одиночный файл: ищем файл с таким же именем в репозитории.
        var matches = tree.Where(p =>
            p.Equals(last, StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith("/" + last, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 0)
        {
            var preferred = matches.FirstOrDefault(p =>
                projectDir.Length > 0 && p.StartsWith(projectDir + "/", StringComparison.OrdinalIgnoreCase));
            return preferred ?? matches[0];
        }

        // Новый файл исходного кода — кладём в папку приложения.
        var extension = Path.GetExtension(last).ToLowerInvariant();
        return SourceExtensions.Contains(extension) ? Join(projectDir, last) : null;
    }

    private static string ParentOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "" : path[..index];
    }

    private static string Join(string folder, params string[] parts)
    {
        var all = new List<string>();
        if (folder.Length > 0) all.Add(folder);
        all.AddRange(parts.Where(p => p.Length > 0));
        return string.Join('/', all);
    }
}
