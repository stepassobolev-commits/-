using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace DynamicIsland;

public sealed record RunInfo(long Id, string Status, string? Conclusion, string Url);

/// <summary>Файл, который нужно записать в репозиторий.</summary>
public sealed class UpdateFile
{
    public UpdateFile(string repoPath, byte[] content, string source)
    {
        RepoPath = repoPath;
        Content = content;
        Source = source;
    }

    public string RepoPath { get; }
    public byte[] Content { get; }
    public string Source { get; }
}

public sealed class GitHubException : Exception
{
    public GitHubException(string message) : base(message) { }
}

/// <summary>Клиент GitHub API: загрузка файлов, запуск сборки, скачивание установщика.</summary>
public sealed class GitHubUpdater : IDisposable
{
    private static readonly Regex ErrorLine = new(
        @"(##\[error\]|\berror\s+[A-Za-z]{1,4}\d+|:\s*error\s|\bError:|Exception:)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _api;
    private readonly HttpClient _plain;

    public GitHubUpdater(string owner, string repo, string token)
    {
        Owner = owner;
        Repo = repo;

        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        _api = new HttpClient(handler, true)
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        _api.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland-Updater/1.0");
        _api.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _api.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        _plain = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _plain.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland-Updater/1.0");
    }

    public string Owner { get; }
    public string Repo { get; }

    private string RepoUrl => $"repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Repo)}";

    public void Dispose()
    {
        _api.Dispose();
        _plain.Dispose();
    }

    // ---------- Адрес репозитория ----------

    /// <summary>Понимает «https://github.com/owner/repo», «owner/repo» и ссылки на подпапки.</summary>
    public static bool TryParseRepository(string? text, out string owner, out string repo)
    {
        owner = "";
        repo = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        var index = value.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        if (index >= 0) value = value[(index + "github.com/".Length)..];

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        owner = parts[0];
        repo = parts[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        return owner.Length > 0 && repo.Length > 0;
    }

    // ---------- Запросы ----------

    private static string Describe(HttpStatusCode status, string body)
    {
        string? apiMessage = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var message)) apiMessage = message.GetString();
        }
        catch { }

        var hint = status switch
        {
            HttpStatusCode.Unauthorized => "Токен недействителен или истёк. Создайте новый токен.",
            HttpStatusCode.Forbidden => "Недостаточно прав у токена (нужны права на содержимое и Actions: чтение и запись) либо превышен лимит запросов.",
            HttpStatusCode.NotFound => "Репозиторий или ветка не найдены. Проверьте адрес репозитория и что токен выдан именно на него.",
            HttpStatusCode.UnprocessableEntity => "GitHub отклонил запрос.",
            _ => $"Ошибка GitHub ({(int)status})."
        };
        return apiMessage is null ? hint : $"{hint} ({apiMessage})";
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _api.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new GitHubException(Describe(response.StatusCode, text));
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct) =>
        SendJsonAsync(HttpMethod.Get, path, null, ct);

    /// <summary>Скачивание файла: GitHub отвечает перенаправлением на временную ссылку.</summary>
    private async Task<byte[]> DownloadAsync(string path, CancellationToken ct)
    {
        using var response = await _api.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
        {
            using var redirected = await _plain.GetAsync(location, ct);
            redirected.EnsureSuccessStatusCode();
            return await redirected.Content.ReadAsByteArrayAsync(ct);
        }

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new GitHubException(Describe(response.StatusCode, text));
        }
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // ---------- Репозиторий ----------

    public async Task<string> GetDefaultBranchAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(RepoUrl, ct);
        return doc.RootElement.GetProperty("default_branch").GetString() ?? "main";
    }

    public async Task<List<string>> GetTreePathsAsync(string branch, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{RepoUrl}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1", ct);
        var result = new List<string>();
        foreach (var item in doc.RootElement.GetProperty("tree").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() == "blob")
                result.Add(item.GetProperty("path").GetString() ?? "");
        }
        return result;
    }

    /// <summary>Записывает все файлы одним коммитом. Возвращает false, если ничего не изменилось.</summary>
    public async Task<bool> CommitFilesAsync(string branch, IReadOnlyList<UpdateFile> files, string message,
        Action<string> log, CancellationToken ct)
    {
        using var refDoc = await GetJsonAsync($"{RepoUrl}/git/ref/heads/{Uri.EscapeDataString(branch)}", ct);
        var headSha = refDoc.RootElement.GetProperty("object").GetProperty("sha").GetString()!;

        using var commitDoc = await GetJsonAsync($"{RepoUrl}/git/commits/{headSha}", ct);
        var baseTree = commitDoc.RootElement.GetProperty("tree").GetProperty("sha").GetString()!;

        var treeItems = new List<object>();
        foreach (var file in files)
        {
            log($"Загрузка: {file.RepoPath}");
            var blob = new { content = Convert.ToBase64String(file.Content), encoding = "base64" };
            using var blobDoc = await SendJsonAsync(HttpMethod.Post, $"{RepoUrl}/git/blobs", blob, ct);
            var blobSha = blobDoc.RootElement.GetProperty("sha").GetString()!;
            treeItems.Add(new { path = file.RepoPath, mode = "100644", type = "blob", sha = blobSha });
        }

        using var treeDoc = await SendJsonAsync(HttpMethod.Post, $"{RepoUrl}/git/trees",
            new { base_tree = baseTree, tree = treeItems }, ct);
        var newTree = treeDoc.RootElement.GetProperty("sha").GetString()!;
        if (newTree == baseTree) return false;

        using var newCommitDoc = await SendJsonAsync(HttpMethod.Post, $"{RepoUrl}/git/commits",
            new { message, tree = newTree, parents = new[] { headSha } }, ct);
        var newCommit = newCommitDoc.RootElement.GetProperty("sha").GetString()!;

        using var updateDoc = await SendJsonAsync(new HttpMethod("PATCH"),
            $"{RepoUrl}/git/refs/heads/{Uri.EscapeDataString(branch)}", new { sha = newCommit, force = false }, ct);
        return true;
    }

    // ---------- Сборка (GitHub Actions) ----------

    public async Task<long> FindWorkflowAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{RepoUrl}/actions/workflows", ct);
        long? fallback = null;
        foreach (var workflow in doc.RootElement.GetProperty("workflows").EnumerateArray())
        {
            if (workflow.GetProperty("state").GetString() != "active") continue;
            var id = workflow.GetProperty("id").GetInt64();
            var name = workflow.GetProperty("name").GetString() ?? "";
            if (name.Contains("installer", StringComparison.OrdinalIgnoreCase)) return id;
            fallback ??= id;
        }
        return fallback ?? throw new GitHubException(
            "В репозитории не найден файл сборки (workflow). Проверьте, что папка .github/workflows загружена.");
    }

    private static RunInfo ParseRun(JsonElement run) => new(
        run.GetProperty("id").GetInt64(),
        run.GetProperty("status").GetString() ?? "",
        run.TryGetProperty("conclusion", out var conclusion) && conclusion.ValueKind == JsonValueKind.String
            ? conclusion.GetString() : null,
        run.GetProperty("html_url").GetString() ?? "");

    public async Task<long> GetLatestRunIdAsync(long workflowId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{RepoUrl}/actions/workflows/{workflowId}/runs?per_page=1", ct);
        var runs = doc.RootElement.GetProperty("workflow_runs");
        return runs.GetArrayLength() == 0 ? 0 : runs[0].GetProperty("id").GetInt64();
    }

    public async Task<RunInfo?> GetLatestSuccessfulRunAsync(long workflowId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{RepoUrl}/actions/workflows/{workflowId}/runs?status=success&per_page=1", ct);
        var runs = doc.RootElement.GetProperty("workflow_runs");
        return runs.GetArrayLength() == 0 ? null : ParseRun(runs[0]);
    }

    public async Task DispatchAsync(long workflowId, string branch, CancellationToken ct)
    {
        using var doc = await SendJsonAsync(HttpMethod.Post, $"{RepoUrl}/actions/workflows/{workflowId}/dispatches",
            new { @ref = branch }, ct);
    }

    /// <summary>Ждёт появления нового запуска сборки. Возвращает null, если за отведённое время его нет.</summary>
    public async Task<RunInfo?> WaitForNewRunAsync(long workflowId, long baselineId, int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(3000, ct);
            using var doc = await GetJsonAsync($"{RepoUrl}/actions/workflows/{workflowId}/runs?per_page=5", ct);
            foreach (var run in doc.RootElement.GetProperty("workflow_runs").EnumerateArray())
            {
                if (run.GetProperty("id").GetInt64() > baselineId) return ParseRun(run);
            }
        }
        return null;
    }

    public async Task<RunInfo> WaitForCompletionAsync(long runId, Action<string> progress, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            using var doc = await GetJsonAsync($"{RepoUrl}/actions/runs/{runId}", ct);
            var info = ParseRun(doc.RootElement);
            if (info.Status == "completed") return info;

            var elapsed = DateTime.UtcNow - started;
            if (elapsed > TimeSpan.FromMinutes(25))
                throw new TimeoutException("Сборка идёт слишком долго. Проверьте её на вкладке Actions в GitHub.");

            progress($"Идёт сборка на GitHub… {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}");
            await Task.Delay(4000, ct);
        }
    }

    /// <summary>Краткое описание ошибки сборки: упавший шаг и строки с ошибками из журнала.</summary>
    public async Task<string> GetFailureSummaryAsync(long runId, CancellationToken ct)
    {
        var sb = new StringBuilder();

        try
        {
            using var jobs = await GetJsonAsync($"{RepoUrl}/actions/runs/{runId}/jobs", ct);
            foreach (var job in jobs.RootElement.GetProperty("jobs").EnumerateArray())
            {
                if (!job.TryGetProperty("steps", out var steps)) continue;
                foreach (var step in steps.EnumerateArray())
                {
                    if (step.TryGetProperty("conclusion", out var stepConclusion) &&
                        stepConclusion.ValueKind == JsonValueKind.String &&
                        stepConclusion.GetString() == "failure")
                    {
                        sb.AppendLine("Упал шаг: " + step.GetProperty("name").GetString());
                    }
                }
            }
        }
        catch { }

        try
        {
            var zip = await DownloadAsync($"{RepoUrl}/actions/runs/{runId}/logs", ct);
            using var archive = new ZipArchive(new MemoryStream(zip));
            var seen = new HashSet<string>();
            var count = 0;
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                using var reader = new StreamReader(entry.Open());
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (!ErrorLine.IsMatch(line)) continue;
                    var clean = Regex.Replace(line, @"^\S+Z\s", "").Trim();
                    if (clean.Length > 400) clean = clean[..400] + "…";
                    if (!seen.Add(clean)) continue;
                    sb.AppendLine(clean);
                    if (++count >= 14) break;
                }
                if (count >= 14) break;
            }
        }
        catch { }

        return sb.Length == 0 ? "Не удалось получить подробности ошибки. Откройте запуск на GitHub." : sb.ToString().TrimEnd();
    }

    /// <summary>Скачивает установщик из результатов сборки и возвращает путь к DynamicIsland-Setup.exe.</summary>
    public async Task<string> DownloadInstallerAsync(long runId, string directory, CancellationToken ct)
    {
        using var list = await GetJsonAsync($"{RepoUrl}/actions/runs/{runId}/artifacts", ct);
        long? artifactId = null;
        foreach (var artifact in list.RootElement.GetProperty("artifacts").EnumerateArray())
        {
            if (artifact.GetProperty("expired").GetBoolean()) continue;
            var name = artifact.GetProperty("name").GetString() ?? "";
            if (name.Contains("installer", StringComparison.OrdinalIgnoreCase))
            {
                artifactId = artifact.GetProperty("id").GetInt64();
                break;
            }
        }
        if (artifactId is null)
            throw new GitHubException("В этой сборке нет установщика (результат не найден или срок хранения истёк).");

        var zipBytes = await DownloadAsync($"{RepoUrl}/actions/artifacts/{artifactId}/zip", ct);
        using var archive = new ZipArchive(new MemoryStream(zipBytes));
        var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new GitHubException("Внутри результата сборки нет файла установщика.");

        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "DynamicIsland-Setup.exe");
        entry.ExtractToFile(target, true);
        return target;
    }
}
