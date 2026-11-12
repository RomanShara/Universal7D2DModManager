namespace U7D2D.Core.Models;

public enum ConflictKind
{
    DuplicateModFolder = 0,
    DuplicateModName = 1,
    SameConfigFile = 2,
    SameXuiWindow = 3,
    DuplicateDll = 4,
    DuplicateHarmony = 5,
    SameAssetBundle = 6,

    /// <summary>Two mods write different values to the same node attribute.</summary>
    ConflictingValue = 7,

    /// <summary>One mod removes a node that another mod edits.</summary>
    RemoveVsModify = 8,

    /// <summary>Two mods add a node with the same identity (same parent and name).</summary>
    DuplicateNode = 9,

    /// <summary>Both mods write the same value: duplication, not a contradiction.</summary>
    RedundantWrite = 10,

    /// <summary>A patch whose xpath finds no target, so its change is silently lost.</summary>
    PatchTargetMissing = 11,

    /// <summary>Mods edit the same element but different attributes, so both changes survive.</summary>
    SameNodeOverlap = 12
}

/// <summary>
/// How two mods relate at one point. Ordered from harmless to harmful, so a list can be sorted
/// and read as "what is actually wrong" instead of "how many rows there are".
/// </summary>
public enum ConflictRelation
{
    /// <summary>Different nodes: both mods apply, nothing overlaps.</summary>
    Coexistence = 0,

    /// <summary>Same element, different attributes: both changes survive, worth knowing.</summary>
    Overlap = 1,

    /// <summary>Outcome depends on load order or on conditions, no hard failure.</summary>
    Potential = 2,

    /// <summary>Changes contradict each other: one of them is lost or the config breaks.</summary>
    Conflict = 3
}

/// <summary>Operation declared by a mod's config patch element.</summary>
public enum PatchOperation
{
    Unknown = 0,
    Append = 1,
    Set = 2,
    SetAttribute = 3,
    Remove = 4,
    RemoveAttribute = 5,
    InsertAfter = 6,
    InsertBefore = 7,
    Replace = 8
}

/// <summary>
/// One patch declared in a mod's config XML, reduced to what is needed to compare mods:
/// which node is touched, through which operation, and with which value.
/// </summary>
public sealed class ConfigPatch
{
    public string File { get; init; } = string.Empty;
    public PatchOperation Operation { get; init; }
    public string RawOperation { get; init; } = string.Empty;
    public string XPath { get; init; } = string.Empty;

    /// <summary>XPath without the trailing attribute step, used as the node identity.</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>Attribute written by the patch, if any (setattribute, or /@attr inside the xpath).</summary>
    public string? Attribute { get; init; }

    /// <summary>New value for set/setattribute; null for removals.</summary>
    public string? Value { get; init; }

    /// <summary>Identities of nodes added by append/insert: parent path plus tag and name.</summary>
    public List<string> AddedNodes { get; init; } = new();

    /// <summary>Raw XML of the nodes added by append/insert, used to replay the patch.</summary>
    public string? InnerXml { get; init; }

    /// <summary>Patch sits inside &lt;conditional&gt;: the game applies it only when the condition holds.</summary>
    public bool IsConditional { get; init; }

    public bool WritesExistingNode => Operation is PatchOperation.Set or PatchOperation.SetAttribute;
    public bool RemovesNode => Operation is PatchOperation.Remove or PatchOperation.RemoveAttribute;
    public bool AddsNode => Operation is PatchOperation.Append or PatchOperation.InsertAfter or PatchOperation.InsertBefore;
}

/// <summary>What one mod does at a conflicting point, with its own evidence.</summary>
public sealed class ConflictParticipant
{
    public string Mod { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string XPath { get; set; } = string.Empty;

    /// <summary>Value in the installed game config before any mod patch, when it could be resolved.</summary>
    public string? OriginalValue { get; set; }

    /// <summary>Value the mod writes.</summary>
    public string? NewValue { get; set; }

    /// <summary>Extra explanation, for example why a patch did not apply.</summary>
    public string? Note { get; set; }

    public bool HasOriginalValue => OriginalValue is not null;
}

public sealed class ConflictEntry
{
    public ConflictKind Kind { get; set; }
    public FindingSeverity Severity { get; set; }
    public string ModA { get; set; } = string.Empty;
    public string ModB { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public double Confidence { get; set; }

    /// <summary>Per-mod evidence: operation, xpath and values. Empty for file-level entries.</summary>
    public List<ConflictParticipant> Participants { get; set; } = new();

    /// <summary>True when the two mods declare each other as a dependency.</summary>
    public bool Related { get; set; }

    /// <summary>Mod whose value survives, when the outcome depends on load order.</summary>
    public string? Winner { get; set; }

    /// <summary>Value that remains after all patches are applied.</summary>
    public string? FinalValue { get; set; }

    /// <summary>Relation between the mods at this point, derived from the kind.</summary>
    public ConflictRelation Relation => Kind switch
    {
        ConflictKind.SameNodeOverlap => ConflictRelation.Overlap,
        ConflictKind.RedundantWrite => ConflictRelation.Overlap,
        ConflictKind.ConflictingValue => ConflictRelation.Potential,
        ConflictKind.DuplicateHarmony => ConflictRelation.Potential,
        _ => ConflictRelation.Conflict
    };

    public bool HasDetails => Participants.Count > 0;
}

public sealed class ConflictReport
{
    public List<ConflictEntry> Conflicts { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    /// <summary>Files several mods patch without touching the same node.</summary>
    public int CoexistenceCount { get; set; }

    /// <summary>Mods in the order the analysis applied them: dependencies first, then folder name.</summary>
    public List<string> LoadOrder { get; set; } = new();

    public int CriticalCount => Conflicts.Count(c => c.Severity == FindingSeverity.Critical);
    public int HighCount => Conflicts.Count(c => c.Severity == FindingSeverity.High);

    public int ConflictCount => Conflicts.Count(c => c.Relation == ConflictRelation.Conflict);
    public int PotentialCount => Conflicts.Count(c => c.Relation == ConflictRelation.Potential);
    public int OverlapCount => Conflicts.Count(c => c.Relation == ConflictRelation.Overlap);

    /// <summary>Confirmed contradictions where the result depends on patch order.</summary>
    public int ConfirmedCount => Conflicts.Count(c => c.Kind is ConflictKind.ConflictingValue
        or ConflictKind.RemoveVsModify or ConflictKind.DuplicateNode);
}

/// <summary>Result of inspecting a game installation directory.</summary>
public sealed class GameInstallInfo
{
    public bool IsValid { get; set; }
    public string GamePath { get; set; } = string.Empty;
    public string? VersionString { get; set; }
    public string? VersionSource { get; set; }
    public string? ManagedAssemblyPath { get; set; }
    public string? ModsPath { get; set; }
    public bool HasModsFolder { get; set; }
    public List<string> Problems { get; set; } = new();
    public List<string> Checks { get; set; } = new();
}

public sealed class BackupRecord
{
    public string Id { get; set; } = string.Empty;
    public string ModFolder { get; set; } = string.Empty;
    public string ArchivePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ModName { get; set; } = string.Empty;
    public string ModVersion { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

public sealed class OperationLogEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}
