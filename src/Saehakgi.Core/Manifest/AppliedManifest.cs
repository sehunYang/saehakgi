namespace Saehakgi.Core.Manifest;

/// <summary>What kind of reversible change an import made on the target PC.</summary>
public enum AppliedActionKind
{
    /// <summary>A path that did not exist before and can simply be deleted on reset.</summary>
    CreatedPath,

    /// <summary>A path that existed and was moved aside to <see cref="AppliedAction.BackupPath"/>.</summary>
    ReplacedPath,

    /// <summary>A registry value that was set; previous state captured for restore.</summary>
    SetRegistryValue,
}

/// <summary>A single reversible step, enough to undo it on reset.</summary>
public sealed class AppliedAction
{
    public AppliedActionKind Kind { get; set; }

    /// <summary>Filesystem path (CreatedPath/ReplacedPath) or human-readable registry target.</summary>
    public string Target { get; set; } = "";

    // ReplacedPath
    public string? BackupPath { get; set; }

    // SetRegistryValue
    public string? RegistryKey { get; set; }
    public string? RegistryValueName { get; set; }
    public bool PreviousExisted { get; set; }
    public string? PreviousValue { get; set; }      // scaffold: Control Panel\Mouse values are REG_SZ
    public string? PreviousValueKind { get; set; }  // RegistryValueKind name
}

/// <summary>
/// The record of everything an import changed on this PC, saved locally so the
/// user can reset the machine back to how it was — reversing only what saehakgi
/// touched, never the pre-existing baseline.
/// </summary>
public sealed class AppliedManifest
{
    public DateTime AppliedUtc { get; set; } = DateTime.UtcNow;
    public string BundlePath { get; set; } = "";
    public List<AppliedAction> Actions { get; set; } = new();
}
