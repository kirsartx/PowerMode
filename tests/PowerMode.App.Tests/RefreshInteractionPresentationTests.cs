using System.Xml.Linq;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class RefreshInteractionPresentationTests
{
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void RefreshButton_DeclaresExplicitKeyboardAndAccessibleEntry()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.xaml"));
        var refreshButton = NamedElement(document, "RefreshButton");

        Assert.Equal("True", AttributeValue(refreshButton, "IsTabStop"));
        Assert.Equal("RefreshButton_Click", AttributeValue(refreshButton, "Click"));
        Assert.Equal("F5", AutomationAttribute(refreshButton, "AcceleratorKey"));

        Assert.Equal(
            1,
            document.Descendants().Count(element =>
                element.Name.LocalName == "KeyboardAccelerator"
                && string.Equals(AttributeValue(element, "Key"), "F5", StringComparison.Ordinal)));
        var accelerator = Assert.Single(
            refreshButton.Descendants(),
            element => element.Name.LocalName == "KeyboardAccelerator");
        Assert.Equal("F5", AttributeValue(accelerator, "Key"));
        Assert.Equal(
            "RefreshKeyboardAccelerator_Invoked",
            AttributeValue(accelerator, "Invoked"));
    }

    [Fact]
    public void RefreshRoute_HandlesKeyboardActivationOnceAndDescribesReadOnlyAction()
    {
        var mainWindowSource = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.xaml.cs"));
        var mainWindow = Minify(mainWindowSource);
        var featureWindow = Minify(File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.Features.cs")));

        Assert.Contains("RefreshKeyboardAccelerator_Invoked", mainWindow);
        Assert.Contains("args.Handled=true", mainWindow);
        Assert.Contains("awaitRefreshStatusAsync()", mainWindow);
        Assert.Contains("AutomationProperties.SetHelpText(RefreshButton", mainWindow);
        Assert.Contains("重新读取状态（只读，F5）", mainWindow);
        Assert.Contains("Reloadstatus(read-only,F5)", mainWindow);

        Assert.DoesNotContain("e.Key==VirtualKey.F5", featureWindow);
        Assert.Contains("VirtualKey.Number1", featureWindow);
        Assert.Contains("VirtualKey.Number4", featureWindow);

        var acceleratorHandler = Minify(MethodBody(
            mainWindowSource,
            "private async void RefreshKeyboardAccelerator_Invoked"));
        Assert.Contains("args.Handled=true", acceleratorHandler);
        Assert.Contains("awaitRefreshStatusAsync()", acceleratorHandler);
        Assert.Equal(1, CountOccurrences(acceleratorHandler, "RefreshStatusAsync("));
        Assert.DoesNotContain("RunModeAsync", acceleratorHandler);

        var refreshMethod = Minify(MethodBody(
            mainWindowSource,
            "private async Task RefreshStatusAsync"));
        Assert.Contains("ReadStateAsync", refreshMethod);
        Assert.Equal(1, CountOccurrences(refreshMethod, "ReadStateAsync("));
        foreach (var mutationCall in new[]
                 {
                     "RunModeAsync(",
                     "TrySwitchAsync(",
                     "ApplyAsync(",
                     "RestoreAsync(",
                     "SetActive("
                 })
        {
            Assert.DoesNotContain(mutationCall, refreshMethod);
        }

        Assert.Contains(
            "varrefreshRevision=_powerStateRevisionGate.BeginRead()",
            refreshMethod);
        Assert.Contains(
            "if(_modeSwitchInProgress||_powerStateRevisionGate.MutationInProgress||Interlocked.CompareExchange(ref_refreshInProgress,1,0)!=0)return",
            refreshMethod);
        Assert.Contains(
            "if(!_powerStateRevisionGate.CanApply(refreshRevision,_modeSwitchInProgress))return",
            refreshMethod);
        Assert.Contains(
            "catch(Exceptionexception){if(!_powerStateRevisionGate.CanApply(refreshRevision,_modeSwitchInProgress))return",
            refreshMethod);

        var readStateCall = refreshMethod.IndexOf(
            "ReadStateAsync(",
            StringComparison.Ordinal);
        var applyRevisionCheck = refreshMethod.IndexOf(
            "CanApply(refreshRevision,_modeSwitchInProgress)",
            StringComparison.Ordinal);
        Assert.True(readStateCall >= 0 && applyRevisionCheck > readStateCall);

        var mutationSource = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.AdvancedFeatures.cs"));
        var mutationMethod = Minify(MethodBody(
            mutationSource,
            "private async Task<bool> RunTargetAfterStartupAsync"));
        var beginMutation = mutationMethod.IndexOf(
            "_powerStateRevisionGate.BeginMutation()",
            StringComparison.Ordinal);
        var setMutationFlag = mutationMethod.IndexOf(
            "_modeSwitchInProgress=true",
            StringComparison.Ordinal);
        Assert.True(beginMutation >= 0 && beginMutation < setMutationFlag);
        Assert.Contains("_powerStateRevisionGate.EndMutation()", mutationMethod);

        var recoverySource = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.AdvancedFeatures.cs"));
        var recoveryMethod = Minify(MethodBody(
            recoverySource,
            "internal async Task<RecoveryActionResult> RestoreBeforeStateAsync"));
        var recoveryMutation = recoveryMethod.IndexOf(
            "_powerStateRevisionGate.BeginMutation()",
            StringComparison.Ordinal);
        var restoreCall = recoveryMethod.IndexOf(
            "GetRecoveryService().RestoreBeforeStateAsync(",
            StringComparison.Ordinal);
        Assert.True(recoveryMutation >= 0 && recoveryMutation < restoreCall);
        Assert.Contains("_powerStateRevisionGate.EndMutation()", recoveryMethod);

        var featuresSource = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.Features.cs"));
        var closingMethod = Minify(MethodBody(
            featuresSource,
            "private async void AppWindow_Closing"));
        var exitMutation = closingMethod.IndexOf(
            "_powerStateRevisionGate.BeginMutation()",
            StringComparison.Ordinal);
        var exitRestore = closingMethod.IndexOf(
            "_exitRestoreCoordinator.RestoreLaunchStateAsync(",
            StringComparison.Ordinal);
        Assert.True(exitMutation >= 0 && exitMutation < exitRestore);
        Assert.Contains("_powerStateRevisionGate.EndMutation()", closingMethod);

        var verifyMethod = Minify(MethodBody(
            mutationSource,
            "internal async Task<LastOperationVerificationResult> VerifyLastOperationAsync"));
        Assert.Contains(
            "varverificationRevision=expectedRevision??_powerStateRevisionGate.BeginRead()",
            verifyMethod);
        Assert.Contains(
            "if(!_powerStateRevisionGate.CanApply(verificationRevision,_modeSwitchInProgress))returnresult",
            verifyMethod);
        Assert.Contains("ResumeStartupAfterRecoveryAsync(cancellationToken)", verifyMethod);

        var verifySummaryMethod = Minify(MethodBody(
            featuresSource,
            "internal async Task VerifyWithSummaryAsync"));
        Assert.Contains(
            "_powerStateRevisionGate.CanApply(verificationRevision,_modeSwitchInProgress)",
            verifySummaryMethod);
        Assert.Contains(
            "ApplyPowerModeState(currentState)",
            verifySummaryMethod);
        Assert.Contains(
            "varverificationRevision=_powerStateRevisionGate.BeginRead()",
            verifySummaryMethod);
        var verificationCheck = verifySummaryMethod.IndexOf(
            "CanApply(verificationRevision,_modeSwitchInProgress)",
            StringComparison.Ordinal);
        var verificationStatus = verifySummaryMethod.IndexOf(
            "StatusText.Text=result.Error",
            verificationCheck,
            StringComparison.Ordinal);
        Assert.True(verificationCheck >= 0 && verificationStatus > verificationCheck);

        var noJournalRead = verifySummaryMethod.IndexOf(
            "varcurrent=await_powerModeBackend.ReadStateAsync(",
            StringComparison.Ordinal);
        var noJournalCheck = verifySummaryMethod.IndexOf(
            "CanApply(verificationRevision,_modeSwitchInProgress)",
            noJournalRead,
            StringComparison.Ordinal);
        Assert.True(noJournalRead >= 0 && noJournalCheck > noJournalRead);
        Assert.Contains(
            "Interlocked.CompareExchange(ref_refreshInProgress,1,0)",
            verifySummaryMethod);
        Assert.Contains("Volatile.Write(ref_refreshInProgress,0)", verifySummaryMethod);

        Assert.Contains("varmutationStarted=false", mutationMethod);
        Assert.Contains("if(mutationStarted)_powerStateRevisionGate.EndMutation()", mutationMethod);
    }

    [Fact]
    public void RefreshStatus_UsesAtomicGateAndRestoresButtonPresentation()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src", "PowerMode.App", "Views", "MainWindow.xaml.cs"));
        var refreshMethod = Minify(MethodBody(source, "private async Task RefreshStatusAsync"));

        Assert.Contains("Interlocked.CompareExchange(ref_refreshInProgress,1,0)", refreshMethod);
        Assert.Contains("RefreshButton.IsEnabled=false", refreshMethod);
        Assert.Contains("RefreshKeyboardAccelerator.IsEnabled=false", refreshMethod);
        Assert.Contains("finally", refreshMethod);
        Assert.Contains("RefreshButton.IsEnabled=refreshButtonWasEnabled", refreshMethod);
        Assert.Contains(
            "RefreshKeyboardAccelerator.IsEnabled=refreshKeyboardAcceleratorWasEnabled",
            refreshMethod);
    }

    private static XElement NamedElement(XDocument document, string name) =>
        Assert.Single(
            document.Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == name);

    private static string? AttributeValue(XElement element, string localName) =>
        (string?)element.Attribute(localName)
        ?? (string?)element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == localName);

    private static string? AutomationAttribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(attribute =>
                attribute.Name.LocalName == localName
                || attribute.Name.LocalName == $"AutomationProperties.{localName}")?.Value;

    private static string Minify(string source) =>
        System.Text.RegularExpressions.Regex.Replace(source, @"\s+", string.Empty);

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private static string MethodBody(string source, string methodName)
    {
        var methodStart = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Could not find method {methodName}.");

        var bodyStart = source.IndexOf('{', methodStart);
        Assert.True(bodyStart >= 0, $"Could not find body for method {methodName}.");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException($"Could not close body for method {methodName}.");
    }

    private static string FindRepositoryFile(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PowerMode.slnx")))
                return Path.Combine([directory.FullName, .. path]);
        }

        throw new DirectoryNotFoundException("Could not locate the PowerMode repository root.");
    }
}
