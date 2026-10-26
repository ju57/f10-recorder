namespace F10Recorder;

internal enum ProfileEntryKind { Existing, New, Correction, Incompatible, Interrupted, Unavailable }

// A profile's editing context is independent of replay pause/readiness.
internal sealed record ProfileEntry(ProfileEntryKind Kind, string Heading, string Reason, bool CanEdit, bool AttentionVisible, bool AutoCheckEngine)
{
    internal static bool HasUsableConfig(Settings config)
    {
        if (config == null || Settings.StoredFields.Any(field => config.FieldIssue(field.Name) != null)) return false;
        return new[] { (config.SaveKey, config.SaveModifiers), (config.OverlayKey, config.OverlayModifiers), (config.ScreenshotKey, config.ScreenshotModifiers) }.Distinct().Count() == 3;
    }
    internal static ProfileEntry Describe(IWindowsRecorder recorder)
    {
        var profile = recorder.ProfileStatus;
        bool unavailable = recorder.Phase is CapturePhase.Reconnecting or CapturePhase.Unknown;
        string reason = profile?.Issues == null ? "Recording preferences could not be verified. Your edits are kept." : profile.Message;
        ProfileEntry Blocked() => new(ProfileEntryKind.Unavailable, "Recording profile unavailable", unavailable
            ? "Recorder unavailable. Your recording preferences and edits are kept."
            : "Recording preferences could not be verified. " + (profile?.Issues?.Length > 0 ? string.Join(" ", profile.Issues) : "Your edits are kept."), false, true, false);
        if (unavailable || profile?.Issues == null || !HasUsableConfig(recorder.Config) || profile.ActivationRequired && !recorder.Paused) return Blocked();
        bool ordinary = profile.CanWrite && !profile.RequiresCorrection && profile.Issues.Length == 0;
        switch (profile.Outcome)
        {
            case "current" when ordinary && profile.ExistingProfile:
                return new(ProfileEntryKind.Existing, profile.ActivationRequired ? "Preferences preserved" : "Your recording preferences", reason, true, profile.ActivationRequired, !profile.ActivationRequired && recorder.Phase != CapturePhase.Recovering);
            case "migration-required" when ordinary && profile.ExistingProfile && profile.ActivationRequired:
                return new(ProfileEntryKind.Existing, "Preferences preserved", reason, true, true, false);
            case "new-profile" when ordinary && !profile.ExistingProfile && profile.ActivationRequired:
                return new(ProfileEntryKind.New, "New recording preferences", "Choose your recording preferences. Recording stays paused until you explicitly resume.", true, true, false);
            case "correction-required" when profile.CanWrite && profile.RequiresCorrection && profile.ActivationRequired && profile.Issues.Length > 0:
                return new(ProfileEntryKind.Correction, "Review recording preferences", reason, true, true, false);
            case "incompatible" when !profile.CanWrite && profile.RequiresCorrection && profile.ActivationRequired && profile.Issues.Length > 0:
                return new(ProfileEntryKind.Incompatible, "Compatible F10 version required", reason, false, true, false);
            case "interrupted" when !profile.CanWrite && profile.RequiresCorrection && profile.ActivationRequired && profile.Issues.Length > 0:
                return new(ProfileEntryKind.Interrupted, "Interrupted profile update", reason, false, true, false);
            default: return Blocked();
        }
    }
}
