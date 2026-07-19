using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerModeWinUI;

/// <summary>
/// Windows integration features that do not depend on the UI layer. The service never changes
/// an OEM battery setting and never downloads or installs an update.
/// </summary>
public sealed class SystemIntegrationService : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int DefaultBackupRetention = 30;
    private static readonly Encoding DiagnosticEncoding = CreateDiagnosticEncoding();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> BackupGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _backupGate;
    private bool _disposed;

    public SystemIntegrationService(
        string appName = "PowerMode",
        string? executablePath = null,
        string? dataDirectory = null,
        HttpClient? httpClient = null,
        Func<UserNotification, bool>? notificationSink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        AppName = appName;
        ExecutablePath = Path.GetFullPath(executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。"));
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName));
        BackupDirectory = Path.Combine(DataDirectory, "backups");
        _backupGate = BackupGates.GetOrAdd(
            Path.GetFullPath(BackupDirectory),
            static _ => new SemaphoreSlim(1, 1));
        NotificationSink = notificationSink;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttpClient = httpClient is null;
    }

    public string AppName { get; }
    public string ExecutablePath { get; }
    public string DataDirectory { get; }
    public string BackupDirectory { get; }
    public string DefaultConfigurationPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>
    /// The MainWindow can assign a callback that fills its existing NOTIFYICONDATA and calls
    /// Shell_NotifyIcon(NIM_MODIFY). Return true only when the balloon was submitted.
    /// </summary>
    public Func<UserNotification, bool>? NotificationSink { get; set; }

    public event EventHandler<UserNotification>? NotificationRequested;

    public StartupRegistration GetStartupRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var command = key?.GetValue(AppName) as string;
        var enabled = !string.IsNullOrWhiteSpace(command);
        return new StartupRegistration(
            enabled,
            enabled && CommandContainsArgument(command!, "--tray"),
            command ?? string.Empty,
            enabled && CommandTargetsExecutable(command!, ExecutablePath));
    }

    /// <summary>Registers the current user only; administrator rights are not required.</summary>
    public StartupRegistration ConfigureStartup(bool enabled, bool startInTray = true)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的开机启动注册表项。 ");

        if (!enabled)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
            return new StartupRegistration(false, false, string.Empty, true);
        }

        if (!File.Exists(ExecutablePath))
            throw new FileNotFoundException("开机启动程序不存在。", ExecutablePath);

        var command = QuoteCommandLineArgument(ExecutablePath) + (startInTray ? " --tray" : string.Empty);
        key.SetValue(AppName, command, RegistryValueKind.String);
        return new StartupRegistration(true, startInTray, command, true);
    }

    /// <summary>
    /// Creates a versioned copy only when the contents differ from the newest retained copy.
    /// This is suitable for calling immediately before or after SettingsStore.Save.
    /// </summary>
    public async Task<ConfigurationBackupResult> BackupConfigurationIfChangedAsync(
        string? configurationPath = null,
        string reason = "automatic",
        int maxBackups = DefaultBackupRetention,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _backupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = Path.GetFullPath(configurationPath ?? DefaultConfigurationPath);
            if (!File.Exists(source))
                return new ConfigurationBackupResult(false, null, "配置文件不存在，无需备份。");

            var sourceHash = await ComputeSha256Async(source, cancellationToken).ConfigureAwait(false);
            var newest = ListConfigurationBackups().FirstOrDefault();
            if (newest is not null && string.Equals(newest.Sha256, sourceHash, StringComparison.OrdinalIgnoreCase))
                return new ConfigurationBackupResult(false, newest, "配置未变化，已保留现有版本。");

            var backup = await CreateConfigurationBackupCoreAsync(source, reason, maxBackups, cancellationToken)
                .ConfigureAwait(false);
            return new ConfigurationBackupResult(true, backup, "已创建配置版本备份。");
        }
        finally
        {
            _backupGate.Release();
        }
    }

    public async Task<ConfigurationBackupInfo> CreateConfigurationBackupAsync(
        string? configurationPath = null,
        string reason = "manual",
        int maxBackups = DefaultBackupRetention,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _backupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateConfigurationBackupCoreAsync(
                configurationPath,
                reason,
                maxBackups,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _backupGate.Release();
        }
    }

    private async Task<ConfigurationBackupInfo> CreateConfigurationBackupCoreAsync(
        string? configurationPath,
        string reason,
        int maxBackups,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(configurationPath ?? DefaultConfigurationPath);
        if (!File.Exists(source)) throw new FileNotFoundException("找不到要备份的配置文件。", source);
        if (maxBackups < 1) throw new ArgumentOutOfRangeException(nameof(maxBackups));

        Directory.CreateDirectory(BackupDirectory);
        var timestamp = DateTimeOffset.UtcNow;
        var safeReason = SanitizeFileComponent(reason, "backup");
        var baseName = SanitizeFileComponent(Path.GetFileNameWithoutExtension(source), "settings");
        var destination = Path.Combine(BackupDirectory,
            $"{baseName}.{timestamp:yyyyMMddTHHmmssfffZ}.{safeReason}.{Guid.NewGuid():N}.json");

        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                         81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var info = await CreateBackupInfoAsync(destination, cancellationToken).ConfigureAwait(false);
        PruneConfigurationBackups(maxBackups);
        return info;
    }

    public IReadOnlyList<ConfigurationBackupInfo> ListConfigurationBackups()
    {
        if (!Directory.Exists(BackupDirectory)) return [];
        var result = new List<ConfigurationBackupInfo>();
        foreach (var path in Directory.EnumerateFiles(BackupDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var file = new FileInfo(path);
                result.Add(new ConfigurationBackupInfo(
                    file.FullName,
                    file.Name,
                    new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                    file.Length,
                    ComputeSha256(path)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(x => x.CreatedUtc).ToArray();
    }

    /// <summary>Restores atomically and makes a safety backup of the current file first.</summary>
    public async Task<ConfigurationRestoreResult> RestoreConfigurationBackupAsync(
        string backupPath,
        string? destinationConfigurationPath = null,
        bool createSafetyBackup = true,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var source = EnsurePathIsInside(backupPath, BackupDirectory);
        if (!File.Exists(source)) throw new FileNotFoundException("配置备份不存在。", source);

        // Use the same strict schema and normalization path as the post-restore reload.
        cancellationToken.ThrowIfCancellationRequested();
        _ = SettingsStore.LoadStrict(source);

        var destination = Path.GetFullPath(destinationConfigurationPath ?? DefaultConfigurationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("配置文件目标目录无效。"));

        ConfigurationBackupInfo? safetyBackup = null;
        if (createSafetyBackup && File.Exists(destination))
            safetyBackup = await CreateConfigurationBackupAsync(destination, "before-restore",
                DefaultBackupRetention, cancellationToken).ConfigureAwait(false);

        var temporary = destination + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destination, overwrite: true);
            return new ConfigurationRestoreResult(true, destination, source, safetyBackup, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDeleteFile(temporary);
            return new ConfigurationRestoreResult(false, destination, source, safetyBackup, ex.Message);
        }
    }

    public async Task<DiagnosticExportResult> ExportDiagnosticPackageAsync(
        DiagnosticExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationZipPath);
        if (request.CommandTimeout <= TimeSpan.Zero || request.CommandTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(request.CommandTimeout));

        var destination = Path.GetFullPath(request.DestinationZipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("诊断包目标目录无效。"));
        var temporary = destination + ".building-" + Guid.NewGuid().ToString("N") + ".tmp";
        var commands = new List<DiagnosticCommandResult>();
        var missing = new List<string>();

        try
        {
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            {
                var manufacturer = DetectManufacturer();
                var manifest = new StringBuilder()
                    .AppendLine($"PowerMode diagnostic package")
                    .AppendLine($"CreatedUtc: {DateTimeOffset.UtcNow:O}")
                    .AppendLine($"AppVersion: {GetCurrentVersionText()}")
                    .AppendLine($"OS: {RuntimeInformation.OSDescription}")
                    .AppendLine($"Architecture: {RuntimeInformation.OSArchitecture}")
                    .AppendLine($"Manufacturer: {manufacturer.Manufacturer}")
                    .AppendLine($"Model: {manufacturer.Model}")
                    .ToString();
                await AddTextEntryAsync(archive, "manifest.txt", manifest, cancellationToken).ConfigureAwait(false);

                var configPath = Path.GetFullPath(request.ConfigurationPath ?? DefaultConfigurationPath);
                await AddFileIfPresentAsync(archive, configPath, "configuration/settings.json", missing,
                    cancellationToken).ConfigureAwait(false);

                var historyFiles = request.HistoryFiles.Count > 0
                    ? request.HistoryFiles
                    : GetDefaultHistoryCandidates();
                var historyIndex = 0;
                foreach (var historyPath in historyFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(historyPath)) continue;
                    var fullPath = Path.GetFullPath(historyPath);
                    var entryName = $"history/{++historyIndex:D2}-{SanitizeFileComponent(Path.GetFileName(fullPath), "history.txt")}";
                    await AddFileIfPresentAsync(archive, fullPath, entryName, missing, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(request.SessionLog))
                    await AddTextEntryAsync(archive, "logs/session-log.txt", request.SessionLog, cancellationToken)
                        .ConfigureAwait(false);

                var commandSpecs = new[]
                {
                    new DiagnosticCommandSpec("powercfg-active", "powercfg.exe", ["/getactivescheme"]),
                    new DiagnosticCommandSpec("powercfg-list", "powercfg.exe", ["/list"]),
                    new DiagnosticCommandSpec("powercfg-query", "powercfg.exe", ["/query"]),
                    new DiagnosticCommandSpec("systeminfo", "systeminfo.exe", ["/FO", "LIST"])
                };

                foreach (var spec in commandSpecs)
                {
                    var result = await RunDiagnosticCommandAsync(spec, request.CommandTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    commands.Add(result);
                    await AddTextEntryAsync(archive, $"system/{spec.Name}.txt", FormatCommandResult(result),
                        cancellationToken).ConfigureAwait(false);
                }

                if (missing.Count > 0)
                    await AddTextEntryAsync(archive, "missing-files.txt", string.Join(Environment.NewLine, missing),
                        cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();
            File.Move(temporary, destination, overwrite: true);
            return new DiagnosticExportResult(destination, commands, missing, commands.Any(x => x.TimedOut));
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }
    }

    /// <summary>Queries release metadata only. It never downloads or launches an installer.</summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        Uri releasesApiUri,
        string? currentVersion = null,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(releasesApiUri);
        if (!IsSafeHttpUri(releasesApiUri))
            throw new ArgumentException("更新 API 必须使用 HTTPS（本机测试地址除外）。", nameof(releasesApiUri));

        var currentText = string.IsNullOrWhiteSpace(currentVersion) ? GetCurrentVersionText() : currentVersion.Trim();
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, releasesApiUri);
            message.Headers.UserAgent.ParseAdd($"{SanitizeUserAgentToken(AppName)}/{SanitizeUserAgentToken(currentText)}");
            message.Headers.Accept.ParseAdd("application/vnd.github+json");
            message.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Failure(currentText,
                    $"GitHub API 返回 {(int)response.StatusCode} {response.ReasonPhrase}。", releasesApiUri);
            if (response.Content.Headers.ContentLength is > 4_000_000)
                return UpdateCheckResult.Failure(currentText, "更新响应过大，已拒绝处理。", releasesApiUri);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var release = SelectRelease(document.RootElement, includePrerelease);
            if (release.ValueKind != JsonValueKind.Object)
                return UpdateCheckResult.Failure(currentText, "没有找到可用的 GitHub Release。", releasesApiUri);

            var tag = GetJsonString(release, "tag_name");
            if (string.IsNullOrWhiteSpace(tag))
                return UpdateCheckResult.Failure(currentText, "GitHub Release 缺少版本标签。", releasesApiUri);
            var releasePage = ToSafeUri(GetJsonString(release, "html_url"));
            var download = SelectPortableAssetUri(release) ?? releasePage;
            var published = TryGetDateTimeOffset(GetJsonString(release, "published_at"));
            var currentParsed = ParseVersion(currentText);
            var latestParsed = ParseVersion(tag);
            var isAvailable = currentParsed is not null && latestParsed is not null
                ? latestParsed > currentParsed
                : !string.Equals(NormalizeVersionLabel(tag), NormalizeVersionLabel(currentText),
                    StringComparison.OrdinalIgnoreCase);

            return new UpdateCheckResult(true, currentText, tag, isAvailable, releasePage, download,
                published, null, releasesApiUri);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failure(currentText, "更新检查超时。", releasesApiUri);
        }
        catch (HttpRequestException ex)
        {
            return UpdateCheckResult.Failure(currentText, ex.Message, releasesApiUri);
        }
        catch (JsonException ex)
        {
            return UpdateCheckResult.Failure(currentText, $"更新响应格式无效：{ex.Message}", releasesApiUri);
        }
    }

    public ManufacturerInfo DetectManufacturer()
    {
        string manufacturer = string.Empty, model = string.Empty;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            manufacturer = FirstNonEmpty(
                key?.GetValue("SystemManufacturer") as string,
                key?.GetValue("BaseBoardManufacturer") as string);
            model = FirstNonEmpty(
                key?.GetValue("SystemProductName") as string,
                key?.GetValue("BaseBoardProduct") as string);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }

        manufacturer = string.IsNullOrWhiteSpace(manufacturer) ? "Unknown" : manufacturer.Trim();
        model = string.IsNullOrWhiteSpace(model) ? "Unknown" : model.Trim();
        var source = manufacturer + " " + model;
        var vendor = source.Contains("lenovo", StringComparison.OrdinalIgnoreCase) ? DeviceVendor.Lenovo
            : source.Contains("asus", StringComparison.OrdinalIgnoreCase)
              || source.Contains("asustek", StringComparison.OrdinalIgnoreCase) ? DeviceVendor.Asus
            : source.Contains("dell", StringComparison.OrdinalIgnoreCase) ? DeviceVendor.Dell
            : source.Contains("hewlett", StringComparison.OrdinalIgnoreCase)
              || source.Contains(" hp", StringComparison.OrdinalIgnoreCase)
              || source.StartsWith("HP", StringComparison.OrdinalIgnoreCase) ? DeviceVendor.Hp
            : DeviceVendor.Other;
        return new ManufacturerInfo(manufacturer, model, vendor);
    }

    /// <summary>
    /// Reports a safe OEM route only. Direct control is deliberately false because Windows has
    /// no common charging-threshold API and undocumented firmware interfaces are unsafe.
    /// </summary>
    public ChargingLimitCapability GetChargingLimitCapability()
    {
        var device = DetectManufacturer();
        return device.Vendor switch
        {
            DeviceVendor.Lenovo => new(device, false, true, "Lenovo Vantage",
                new Uri("https://www.lenovo.com/vantage"),
                "Windows 没有通用充电上限接口。请在 Lenovo Vantage 中查看“养护模式/充电阈值”；是否支持取决于具体机型。",
                "Windows has no universal charge-limit API. Check Conservation Mode or charging thresholds in Lenovo Vantage; support depends on the model."),
            DeviceVendor.Asus => new(device, false, true, "MyASUS",
                new Uri("https://www.asus.com/support/myasus-deeplink/"),
                "Windows 没有通用充电上限接口。请在 MyASUS 的电池健康充电设置中查看可用选项；是否支持取决于具体机型。",
                "Windows has no universal charge-limit API. Check Battery Health Charging in MyASUS; support depends on the model."),
            DeviceVendor.Dell => new(device, false, true, "Dell Power Manager / Dell Optimizer",
                new Uri("https://www.dell.com/support/kbdoc/000177768/guide-to-dell-power-manager"),
                "Windows 没有通用充电上限接口。请使用 Dell Power Manager、Dell Optimizer 或 BIOS 的电池设置；是否支持取决于具体机型。",
                "Windows has no universal charge-limit API. Check Dell Power Manager, Dell Optimizer, or BIOS battery settings; support depends on the model."),
            DeviceVendor.Hp => new(device, false, true, "HP BIOS / HP Support",
                new Uri("https://support.hp.com/"),
                "Windows 没有通用充电上限接口。部分 HP 商用机型可在 BIOS 的 Battery Health Manager 中设置；消费机型可能不提供固定阈值。",
                "Windows has no universal charge-limit API. Some HP business models expose Battery Health Manager in BIOS; consumer models may not offer a fixed threshold."),
            _ => new(device, false, false, null, null,
                "Windows 没有通用充电上限接口，且未识别到可安全推荐的厂商工具。请查看电脑 BIOS 或厂商支持应用。PowerMode 不会调用未知固件接口。",
                "Windows has no universal charge-limit API and no safe vendor utility was identified. Check the BIOS or OEM support app. PowerMode will not call undocumented firmware interfaces.")
        };
    }

    /// <summary>
    /// Requests a visible notification through the injected tray callback. When no callback is
    /// available, it emits a non-blocking Windows sound only.
    /// </summary>
    public NotificationDeliveryResult Notify(UserNotification notification)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(notification);
        var delivered = false;
        try { delivered = NotificationSink?.Invoke(notification) == true; }
        catch { delivered = false; }

        if (!delivered && NotificationRequested is not null)
        {
            try
            {
                NotificationRequested.Invoke(this, notification);
                delivered = true;
            }
            catch { delivered = false; }
        }

        if (delivered) return new NotificationDeliveryResult(true, false);
        var beeped = NativeMethods.MessageBeep(notification.Severity switch
        {
            NotificationSeverity.Error => 0x00000010,
            NotificationSeverity.Warning => 0x00000030,
            _ => 0x00000040
        });
        return new NotificationDeliveryResult(false, beeped);
    }

    /// <summary>Produces values compatible with the szInfo fields of NOTIFYICONDATA.</summary>
    public static TrayBalloonData CreateTrayBalloonData(UserNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var flags = notification.Severity switch
        {
            NotificationSeverity.Warning => 0x00000002u, // NIIF_WARNING
            NotificationSeverity.Error => 0x00000003u,   // NIIF_ERROR
            _ => 0x00000001u                              // NIIF_INFO
        };
        return new TrayBalloonData(
            TruncateForNativeField(notification.Title, 63),
            TruncateForNativeField(notification.Message, 255),
            (uint)Math.Clamp(notification.DurationMilliseconds, 1000, 30_000),
            flags,
            0x00000010u); // NIF_INFO
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_ownsHttpClient) _httpClient.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void PruneConfigurationBackups(int maxBackups)
    {
        foreach (var backup in ListConfigurationBackups().Skip(maxBackups))
            TryDeleteFile(backup.Path);
    }

    private async Task<ConfigurationBackupInfo> CreateBackupInfoAsync(string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        return new ConfigurationBackupInfo(file.FullName, file.Name,
            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), file.Length,
            await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false));
    }

    private async Task<DiagnosticCommandResult> RunDiagnosticCommandAsync(
        DiagnosticCommandSpec spec,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            var psi = new ProcessStartInfo(spec.FileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = DiagnosticEncoding,
                StandardErrorEncoding = DiagnosticEncoding
            };
            foreach (var argument in spec.Arguments) psi.ArgumentList.Add(argument);
            using var process = Process.Start(psi);
            if (process is null)
                return new DiagnosticCommandResult(spec.Name, spec.FileName, -1, string.Empty,
                    "无法启动命令。", false, DateTimeOffset.UtcNow - started);

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                TryKillProcess(process);
                using var killWaitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await process.WaitForExitAsync(killWaitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // A protected or wedged child must not make diagnostic export hang forever.
                    ObserveFault(outputTask);
                    ObserveFault(errorTask);
                    return new DiagnosticCommandResult(
                        spec.Name,
                        spec.FileName + " " + string.Join(' ', spec.Arguments.Select(QuoteCommandLineArgument)),
                        -1,
                        string.Empty,
                        "命令执行超时，且进程未能在终止请求后退出。",
                        true,
                        DateTimeOffset.UtcNow - started);
                }
            }
            catch
            {
                TryKillProcess(process);
                throw;
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new DiagnosticCommandResult(spec.Name,
                spec.FileName + " " + string.Join(' ', spec.Arguments.Select(QuoteCommandLineArgument)),
                timedOut ? -1 : process.ExitCode, output, error, timedOut, DateTimeOffset.UtcNow - started);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DiagnosticCommandResult(spec.Name, spec.FileName, -1, string.Empty,
                "命令执行超时。", true, DateTimeOffset.UtcNow - started);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DiagnosticCommandResult(spec.Name, spec.FileName, -1, string.Empty,
                ex.ToString(), false, DateTimeOffset.UtcNow - started);
        }
    }

    private static string FormatCommandResult(DiagnosticCommandResult result)
    {
        var builder = new StringBuilder()
            .AppendLine($"> {result.CommandLine}")
            .AppendLine($"ExitCode: {result.ExitCode}")
            .AppendLine($"TimedOut: {result.TimedOut}")
            .AppendLine($"Duration: {result.Duration.TotalMilliseconds:F0} ms")
            .AppendLine()
            .AppendLine(result.StandardOutput.TrimEnd());
        if (!string.IsNullOrWhiteSpace(result.StandardError))
            builder.AppendLine().AppendLine("--- stderr ---").AppendLine(result.StandardError.TrimEnd());
        return builder.ToString();
    }

    private IReadOnlyList<string> GetDefaultHistoryCandidates() =>
    [
        Path.Combine(DataDirectory, "switch-history.jsonl"),
        Path.Combine(DataDirectory, "history.json"),
        Path.Combine(DataDirectory, "history.jsonl"),
        Path.Combine(DataDirectory, "operation-history.json")
    ];

    private static async Task AddTextEntryAsync(ZipArchive archive, string entryName, string text,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = DateTimeOffset.Now;
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: false);
        await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddFileIfPresentAsync(ZipArchive archive, string sourcePath, string entryName,
        ICollection<string> missing, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            missing.Add(sourcePath);
            return;
        }

        var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(File.GetLastWriteTime(sourcePath));
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = entry.Open();
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement SelectRelease(JsonElement root, bool includePrerelease)
    {
        if (root.ValueKind == JsonValueKind.Object)
            return IsEligibleRelease(root, includePrerelease) ? root : default;
        if (root.ValueKind != JsonValueKind.Array) return default;
        foreach (var release in root.EnumerateArray())
            if (release.ValueKind == JsonValueKind.Object && IsEligibleRelease(release, includePrerelease))
                return release;
        return default;
    }

    private static bool IsEligibleRelease(JsonElement release, bool includePrerelease)
    {
        var draft = release.TryGetProperty("draft", out var draftElement) && draftElement.ValueKind == JsonValueKind.True;
        var prerelease = release.TryGetProperty("prerelease", out var preElement) && preElement.ValueKind == JsonValueKind.True;
        return !draft && (includePrerelease || !prerelease);
    }

    private static Uri? SelectPortableAssetUri(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        var candidates = assets.EnumerateArray()
            .Select(asset => new
            {
                Name = GetJsonString(asset, "name"),
                Url = ToSafeUri(GetJsonString(asset, "browser_download_url"))
            })
            .Where(x => x.Url is not null)
            .OrderByDescending(x => x.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Name.Contains("portable", StringComparison.OrdinalIgnoreCase));
        return candidates.FirstOrDefault()?.Url;
    }

    private static string GetJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static Version? ParseVersion(string? text)
    {
        var normalized = NormalizeVersionLabel(text);
        var separator = normalized.IndexOfAny(['-', '+']);
        if (separator >= 0) normalized = normalized[..separator];
        return Version.TryParse(normalized, out var version) ? version : null;
    }

    private static string NormalizeVersionLabel(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return value.StartsWith('v') || value.StartsWith('V') ? value[1..] : value;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(string value) =>
        DateTimeOffset.TryParse(value, out var result) ? result : null;

    private static Uri? ToSafeUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsSafeHttpUri(uri) ? uri : null;

    private static bool IsSafeHttpUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps
        || (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)));

    private static string GetCurrentVersionText() =>
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version?.ToString(3) ?? "0.0.0";

    private static string SanitizeUserAgentToken(string text)
    {
        var result = new string(text.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'
            ? c : '-').ToArray()).Trim('-');
        return string.IsNullOrEmpty(result) ? "unknown" : result;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    private static string QuoteCommandLineArgument(string value)
    {
        if (value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c != '"')) return value;
        var builder = new StringBuilder(value.Length + 2).Append('"');
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') builder.Append('\\', slashes * 2 + 1).Append('"');
            else builder.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return builder.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static bool CommandTargetsExecutable(string command, string executablePath)
    {
        try
        {
            var trimmed = command.TrimStart();
            if (trimmed.StartsWith('"'))
            {
                var end = trimmed.IndexOf('"', 1);
                return end > 1 && string.Equals(Path.GetFullPath(trimmed[1..end]),
                    Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
            }
            var firstSpace = trimmed.IndexOf(' ');
            var target = firstSpace < 0 ? trimmed : trimmed[..firstSpace];
            return string.Equals(Path.GetFullPath(target), Path.GetFullPath(executablePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool CommandContainsArgument(string command, string argument) =>
        command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Any(x => string.Equals(x.Trim('"'), argument, StringComparison.OrdinalIgnoreCase));

    private static string SanitizeFileComponent(string? value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string((value ?? string.Empty).Where(c => !invalid.Contains(c) && !char.IsControl(c))
            .Select(c => char.IsWhiteSpace(c) ? '-' : c).Take(80).ToArray()).Trim('.', '-', ' ');
        return string.IsNullOrEmpty(result) ? fallback : result;
    }

    private static string EnsurePathIsInside(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能恢复 PowerMode 备份目录内的配置版本。 ");
        return fullPath;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Encoding CreateDiagnosticEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)NativeMethods.GetOEMCP());
        }
        catch { return Encoding.UTF8; }
    }

    private static void TryKillProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string TruncateForNativeField(string? text, int maximumLength)
    {
        var value = (text ?? string.Empty).Replace('\0', ' ').Trim();
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record DiagnosticCommandSpec(string Name, string FileName, IReadOnlyList<string> Arguments);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern uint GetOEMCP();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MessageBeep(uint type);
    }
}

public sealed record StartupRegistration(
    bool Enabled,
    bool StartInTray,
    string Command,
    bool TargetsCurrentExecutable);

public sealed record ConfigurationBackupInfo(
    string Path,
    string FileName,
    DateTimeOffset CreatedUtc,
    long SizeBytes,
    string Sha256);

public sealed record ConfigurationBackupResult(
    bool Created,
    ConfigurationBackupInfo? Backup,
    string Message);

public sealed record ConfigurationRestoreResult(
    bool Succeeded,
    string DestinationPath,
    string BackupPath,
    ConfigurationBackupInfo? SafetyBackup,
    string? Error);

public sealed class DiagnosticExportRequest
{
    public required string DestinationZipPath { get; init; }
    public string? ConfigurationPath { get; init; }
    public IReadOnlyList<string> HistoryFiles { get; init; } = [];
    public string? SessionLog { get; init; }
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

public sealed record DiagnosticCommandResult(
    string Name,
    string CommandLine,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    TimeSpan Duration);

public sealed record DiagnosticExportResult(
    string ZipPath,
    IReadOnlyList<DiagnosticCommandResult> Commands,
    IReadOnlyList<string> MissingFiles,
    bool HadTimeouts);

public sealed record UpdateCheckResult(
    bool Succeeded,
    string CurrentVersion,
    string LatestVersion,
    bool IsUpdateAvailable,
    Uri? ReleasePageUri,
    Uri? PortableDownloadUri,
    DateTimeOffset? PublishedAt,
    string? Error,
    Uri ApiUri)
{
    internal static UpdateCheckResult Failure(string currentVersion, string error, Uri apiUri) =>
        new(false, currentVersion, string.Empty, false, null, null, null, error, apiUri);
}

public enum DeviceVendor
{
    Other,
    Lenovo,
    Asus,
    Dell,
    Hp
}

public sealed record ManufacturerInfo(string Manufacturer, string Model, DeviceVendor Vendor);

public sealed record ChargingLimitCapability(
    ManufacturerInfo Device,
    bool DirectControlSupported,
    bool VendorUtilityMaySupport,
    string? RecommendedTool,
    Uri? SupportUri,
    string DescriptionZh,
    string DescriptionEn);

public enum NotificationSeverity
{
    Information,
    Success,
    Warning,
    Error
}

public sealed record UserNotification(
    string Title,
    string Message,
    NotificationSeverity Severity = NotificationSeverity.Information,
    int DurationMilliseconds = 5000);

public sealed record TrayBalloonData(
    string Title,
    string Message,
    uint TimeoutMilliseconds,
    uint InfoFlags,
    uint RequiredNotifyIconFlags);

public sealed record NotificationDeliveryResult(bool DeliveredToUi, bool UsedFallbackBeep);
