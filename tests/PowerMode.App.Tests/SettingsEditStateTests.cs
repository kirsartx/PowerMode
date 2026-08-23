using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class SettingsEditStateTests
{
    [Fact]
    public void DirtyValidState_CanSave_AndSuccessfulSaveClearsIt()
    {
        var state = new SettingsEditState();

        state.MarkChanged();
        state.SetInputValid(true);

        Assert.True(state.CanSave);
        state.MarkSaved();
        Assert.False(state.IsDirty);
        Assert.False(state.CanSave);
    }

    [Fact]
    public void ResolveClose_SaveRequiresDirtyValidInput()
    {
        var state = new SettingsEditState();
        state.MarkChanged();
        state.SetInputValid(false);

        Assert.Equal(
            SettingsCloseAction.KeepOpen,
            state.ResolveClose(SettingsCloseChoice.Save));

        state.SetInputValid(true);
        Assert.Equal(
            SettingsCloseAction.SaveAndClose,
            state.ResolveClose(SettingsCloseChoice.Save));
    }

    [Fact]
    public void OpeningSession_DeepClonesOwnerSettings()
    {
        var owner = CreateSettings("Owner profile", "Owner rule");
        var session = new SettingsEditSession(owner, (_, _) => Task.CompletedTask, _ => { });

        session.WorkingCopy.Profiles[0].Name = "Working profile";
        session.WorkingCopy.Rules[0].Conditions[0].Value = "25";

        Assert.Equal("Owner profile", owner.Profiles[0].Name);
        Assert.Equal("30", owner.Rules[0].Conditions[0].Value);
        Assert.NotSame(owner.Profiles, session.WorkingCopy.Profiles);
        Assert.NotSame(owner.Rules[0].Conditions, session.WorkingCopy.Rules[0].Conditions);
    }

    [Fact]
    public async Task SaveAsync_PersistsFirst_ThenAppliesIndependentOwnerAndWorkingClones()
    {
        var owner = CreateSettings("Owner profile", "Owner rule");
        PowerModeSettings? persisted = null;
        PowerModeSettings? applied = null;
        var persistenceCompleted = false;
        var session = new SettingsEditSession(
            owner,
            (snapshot, _) =>
            {
                persisted = snapshot;
                persistenceCompleted = true;
                return Task.CompletedTask;
            },
            snapshot =>
            {
                Assert.True(persistenceCompleted);
                applied = snapshot;
            });
        session.Mutate(settings =>
        {
            settings.Profiles[0].Name = "Edited profile";
            settings.Rules[0].Name = "Edited rule";
        });

        var originalWorking = session.WorkingCopy;
        var result = await session.SaveAsync();

        Assert.True(result.Succeeded);
        Assert.False(session.State.IsDirty);
        Assert.NotNull(persisted);
        Assert.NotNull(applied);
        Assert.NotSame(originalWorking, persisted);
        Assert.NotSame(originalWorking, applied);
        Assert.NotSame(originalWorking, session.WorkingCopy);
        Assert.NotSame(persisted, applied);
        Assert.NotSame(persisted!.Profiles, applied!.Profiles);
        Assert.NotSame(applied.Profiles, session.WorkingCopy.Profiles);
        Assert.NotSame(persisted.Rules, applied.Rules);
        Assert.NotSame(
            applied.Rules[0].Conditions,
            session.WorkingCopy.Rules[0].Conditions);
        Assert.Equal("Edited profile", persisted.Profiles[0].Name);

        persisted.Profiles[0].Name = "Persisted mutation";
        applied.Profiles[0].Name = "Owner mutation";
        Assert.Equal("Edited profile", session.WorkingCopy.Profiles[0].Name);
    }

    [Fact]
    public async Task SaveAsync_PersistenceFailurePreservesOwnerInputWorkingIdentityAndDirtyState()
    {
        var owner = CreateSettings("Owner profile", "Owner rule");
        PowerModeSettings? applied = null;
        var session = new SettingsEditSession(
            owner,
            (_, _) => throw new IOException("disk full"),
            snapshot => applied = snapshot);
        session.Mutate(settings => settings.Profiles[0].Name = "Unsaved profile");
        session.State.SetInputValid(true);
        var workingBefore = session.WorkingCopy;

        var result = await session.SaveAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("disk full", result.Error);
        Assert.Null(applied);
        Assert.Same(workingBefore, session.WorkingCopy);
        Assert.Equal("Unsaved profile", session.WorkingCopy.Profiles[0].Name);
        Assert.Equal("Owner profile", owner.Profiles[0].Name);
        Assert.True(session.State.IsDirty);
        Assert.True(session.State.IsInputValid);
    }

    [Fact]
    public async Task ProfileRuleAndImportMutationsStayWorkingOnlyUntilSave()
    {
        var owner = CreateSettings("Owner profile", "Owner rule");
        PowerModeSettings? applied = null;
        var session = new SettingsEditSession(
            owner,
            (_, _) => Task.CompletedTask,
            snapshot => applied = snapshot);

        session.Mutate(settings =>
        {
            settings.Profiles.Add(new CustomPowerProfile { Name = "Added" });
            settings.Rules[0].IsEnabled = false;
            settings.Rules.Add(new AutomationRule { Name = "Added rule" });
            settings.Rules[0].Name = "Edited working rule";
            settings.Rules.RemoveAt(1);
        });
        Assert.Single(owner.Profiles);
        Assert.Single(owner.Rules);
        Assert.True(owner.Rules[0].IsEnabled);
        Assert.Null(applied);

        var imported = CreateSettings("Imported", "Imported rule");
        session.ReplaceWorkingCopy(imported, markDirty: true);
        imported.Profiles[0].Name = "Caller mutation";
        Assert.Equal("Imported", session.WorkingCopy.Profiles[0].Name);
        Assert.Equal("Owner profile", owner.Profiles[0].Name);

        Assert.True((await session.SaveAsync()).Succeeded);
        Assert.Equal("Imported", applied!.Profiles[0].Name);
    }

    [Fact]
    public void ConfirmedMutation_CancelDoesNotMutate_ConfirmMutatesWorkingOnly()
    {
        var owner = CreateSettings("Keep me", "Keep rule");
        var session = new SettingsEditSession(owner, (_, _) => Task.CompletedTask, _ => { });

        Assert.False(session.MutateWhenConfirmed(false, settings => settings.Profiles.Clear()));
        Assert.Single(session.WorkingCopy.Profiles);
        Assert.False(session.State.IsDirty);

        Assert.True(session.MutateWhenConfirmed(true, settings => settings.Profiles.Clear()));
        Assert.Empty(session.WorkingCopy.Profiles);
        Assert.Single(owner.Profiles);
        Assert.True(session.State.IsDirty);
    }

    [Fact]
    public void ExportSnapshot_UsesWorkingCopyWithoutClearingDirty()
    {
        var session = new SettingsEditSession(
            CreateSettings("Owner", "Rule"),
            (_, _) => Task.CompletedTask,
            _ => { });
        session.Mutate(settings => settings.Profiles[0].Name = "Export me");

        var export = session.CreateExportSnapshot();

        Assert.Equal("Export me", export.Profiles[0].Name);
        Assert.NotSame(export, session.WorkingCopy);
        Assert.NotSame(export.Profiles, session.WorkingCopy.Profiles);
        Assert.True(session.State.IsDirty);
    }

    [Fact]
    public void RestoredBackup_ReplacesWorkingCopyWithCloneAndMarksClean()
    {
        var session = new SettingsEditSession(
            CreateSettings("Owner", "Rule"),
            (_, _) => Task.CompletedTask,
            _ => { });
        session.Mutate(settings => settings.Profiles[0].Name = "Dirty");
        var restored = CreateSettings("Restored", "Restored rule");

        session.ReplaceWorkingCopy(restored, markDirty: false);

        Assert.Equal("Restored", session.WorkingCopy.Profiles[0].Name);
        Assert.NotSame(restored, session.WorkingCopy);
        Assert.False(session.State.IsDirty);
    }

    [Theory]
    [InlineData((int)SettingsCloseChoice.Discard, true, 0)]
    [InlineData((int)SettingsCloseChoice.ContinueEditing, false, 0)]
    [InlineData((int)SettingsCloseChoice.Save, true, 1)]
    public async Task CloseCoordinator_UsesGuardedChoiceSemantics(
        int choiceValue,
        bool expectedClose,
        int expectedSaves)
    {
        var saveCount = 0;
        var state = new SettingsEditState();
        state.MarkChanged();
        var coordinator = new SettingsCloseCoordinator(state, () =>
        {
            saveCount++;
            state.MarkSaved();
            return Task.FromResult(true);
        });

        var shouldClose = await coordinator.RequestCloseAsync(_ =>
            Task.FromResult((SettingsCloseChoice)choiceValue));

        Assert.Equal(expectedClose, shouldClose);
        Assert.Equal(expectedSaves, saveCount);
    }

    [Fact]
    public async Task CloseCoordinator_InvalidOrFailedSaveKeepsWindowOpen()
    {
        var state = new SettingsEditState();
        state.MarkChanged();
        state.SetInputValid(false);
        var saveCount = 0;
        var coordinator = new SettingsCloseCoordinator(state, () =>
        {
            saveCount++;
            return Task.FromResult(false);
        });

        var invalidClose = await coordinator.RequestCloseAsync(_ =>
            Task.FromResult(SettingsCloseChoice.Save));
        state.SetInputValid(true);
        var failedClose = await coordinator.RequestCloseAsync(_ =>
            Task.FromResult(SettingsCloseChoice.Save));

        Assert.False(invalidClose);
        Assert.False(failedClose);
        Assert.Equal(1, saveCount);
        Assert.True(state.IsDirty);
    }

    [Fact]
    public async Task CloseCoordinator_CleanWindowClosesWithoutPrompt()
    {
        var prompted = false;
        var coordinator = new SettingsCloseCoordinator(
            new SettingsEditState(),
            () => Task.FromResult(true));

        var shouldClose = await coordinator.RequestCloseAsync(_ =>
        {
            prompted = true;
            return Task.FromResult(SettingsCloseChoice.ContinueEditing);
        });

        Assert.True(shouldClose);
        Assert.False(prompted);
    }

    private static PowerModeSettings CreateSettings(string profileName, string ruleName) =>
        new()
        {
            Profiles = [new CustomPowerProfile { Name = profileName }],
            Rules =
            [
                new AutomationRule
                {
                    Name = ruleName,
                    Conditions =
                    [
                        new RuleCondition
                        {
                            Type = RuleConditionType.BatteryLevel,
                            Value = "30"
                        }
                    ]
                }
            ]
        };
}
