using System.Text.RegularExpressions;
using U7D2D.Core.Archives;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.I18n;
using U7D2D.Core.Models;

namespace U7D2D.Core.Analysis;

/// <summary>
/// Static compatibility analysis. It never claims runtime compatibility: the verdict is derived
/// from actual mod content (DLL metadata, XML overrides, declared dependencies) and explicitly
/// reports confidence and what was not verified.
/// </summary>
public sealed class CompatibilityAnalyzer
{
    private const long MaxDllBytes = 64L * 1024 * 1024;
    private const long MaxConfigBytes = 16L * 1024 * 1024;
    private const int MaxDllCount = 24;
    private const int MaxConfigCount = 80;

    private static readonly Regex LegacyVersionRegex = new(@"(?i)\b(alpha|a1[0-9]|a2[0-9]|b[0-9]{2,3})\b", RegexOptions.Compiled);

    private readonly AppPaths _paths;

    public CompatibilityAnalyzer(AppPaths paths) => _paths = paths;

    public CompatibilityReport Analyze(
        ModPackage package,
        AppSettings settings,
        GameInstallInfo game,
        IReadOnlyList<InstalledMod> installed,
        CancellationToken cancellationToken = default)
    {
        var report = new CompatibilityReport
        {
            ModName = package.DisplayTitle,
            ModVersion = package.VersionLabel,
            GameVersion = game.VersionString ?? settings.GameVersionOverride,
            AnalyzedUtc = DateTime.UtcNow
        };

        var findings = report.Findings;

        if (!game.IsValid)
        {
            report.Verdict = CompatibilityVerdict.Unknown;
            report.Confidence = 0;
            report.Headline = Localization.S("ca.noGameHeadline");
            report.Summary = Localization.S("ca.noGameSummary");
            findings.Add(new AnalysisFinding
            {
                Severity = FindingSeverity.High,
                Category = Localization.S("ca.catGame"),
                Title = Localization.S("ca.gameNotFoundTitle"),
                Detail = Localization.S("ca.gameNotFoundDetail"),
                Evidence = string.Join("; ", game.Problems.DefaultIfEmpty(Localization.S("ca.pathNotSet"))),
                Confidence = 1
            });
            report.NotVerified.Add(Localization.S("ca.nvAllChecks"));
            return report;
        }

        report.SignalsUsed.Add(Localization.SF("ca.sigGame", game.GamePath));
        report.SignalsUsed.Add(Localization.SF("ca.sigGameVersion",
            game.VersionString ?? Localization.S("ca.versionUndefined"),
            game.VersionSource ?? Localization.S("ca.versionNoSource")));

        var api = game.ManagedAssemblyPath is not null ? GameApiIndex.Load(game.ManagedAssemblyPath) : null;
        if (api is not null)
            report.SignalsUsed.Add(Localization.SF("ca.sigApi", api.TypeCount, api.AssemblyVersion, api.RuntimeFamily));
        else
            report.NotVerified.Add(Localization.S("ca.nvApiCheck"));

        var dlls = new List<ModAssemblyAnalysis>();
        var configs = new List<ConfigFileAnalysis>();
        var shipsHarmony = package.Content.DllFiles.Any(d =>
            Path.GetFileName(d).Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase));

        try
        {
            using var handle = ArchiveReader.Open(package.ArchivePath, _paths.TempDirectory, cancellationToken);
            var prefix = package.RootInArchive;

            var dllEntries = handle.Entries
                .Where(e => !e.IsDirectory && IsUnderRoot(e.Key, prefix) && e.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Where(e => e.Size <= MaxDllBytes)
                .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .Take(MaxDllCount)
                .ToList();

            foreach (var entry in dllEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var bytes = handle.ReadEntryBytes(entry.Key, MaxDllBytes);
                    dlls.Add(AssemblyInspector.Analyze(bytes, Path.GetFileName(entry.Key), api));
                }
                catch (Exception ex)
                {
                    findings.Add(new AnalysisFinding
                    {
                        Severity = FindingSeverity.Low,
                        Category = "DLL",
                        Title = Localization.SF("ca.dllReadFailed", Path.GetFileName(entry.Key)),
                        Detail = Localization.S("ca.dllSkipped"),
                        Evidence = ex.Message,
                        Confidence = 0.9
                    });
                }
            }

            var configEntries = handle.Entries
                .Where(e => !e.IsDirectory && IsUnderRoot(e.Key, prefix) && IsConfigXml(e.Key))
                .Where(e => e.Size <= MaxConfigBytes)
                .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .Take(MaxConfigCount)
                .ToList();

            foreach (var entry in configEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var bytes = handle.ReadEntryBytes(entry.Key, MaxConfigBytes);
                    var relative = prefix.Length == 0 ? entry.Key : entry.Key[(prefix.Length + 1)..];
                    configs.Add(ConfigFileAnalysis.Parse(relative, TextUtil.Decode(bytes)));
                }
                catch (Exception ex)
                {
                    findings.Add(new AnalysisFinding
                    {
                        Severity = FindingSeverity.Low,
                        Category = Localization.S("ca.catConfig"),
                        Title = Localization.SF("ca.dllReadFailed", Path.GetFileName(entry.Key)),
                        Evidence = ex.Message,
                        Confidence = 0.9
                    });
                }
            }
        }
        catch (ArchiveOpenException ex)
        {
            report.Verdict = CompatibilityVerdict.Unknown;
            report.Confidence = 0;
            report.Headline = Localization.S("ca.archiveUnreadable");
            report.Summary = Localization.S("ca.archiveUnreadableSummary");
            findings.Add(new AnalysisFinding
            {
                Severity = FindingSeverity.High,
                Category = Localization.S("ca.catArchive"),
                Title = Localization.S("ca.archiveReadError"),
                Detail = ex.Message,
                Evidence = package.ArchivePath,
                Confidence = 1
            });
            return report;
        }

        report.SignalsUsed.Add(Localization.SF("ca.sigCounts", dlls.Count, configs.Count));

        AnalyzeAssemblies(dlls, api, shipsHarmony, game, installed, findings, report);
        AnalyzeConfigs(configs, findings, report);
        AnalyzeDeclaredVersion(package, game, settings, findings);
        AnalyzeDependencies(package, installed, findings);

        ComposeVerdict(report, dlls, configs, api);

        report.Findings.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        return report;
    }

    private static void AnalyzeAssemblies(
        List<ModAssemblyAnalysis> dlls,
        GameApiIndex? api,
        bool shipsHarmony,
        GameInstallInfo game,
        IReadOnlyList<InstalledMod> installed,
        List<AnalysisFinding> findings,
        CompatibilityReport report)
    {
        if (dlls.Count == 0) return;

        foreach (var dll in dlls.Where(d => !d.Loadable))
        {
            findings.Add(new AnalysisFinding
            {
                Severity = FindingSeverity.Medium,
                Category = "DLL",
                Title = Localization.SF("ca.dllNotParsedTitle", dll.FileName),
                Detail = Localization.S("ca.dllNotParsedDetail"),
                Evidence = dll.Error ?? Localization.S("ca.noDetails"),
                Confidence = 0.9
            });
        }

        var missingTypes = dlls.SelectMany(d => d.MissingGameTypes).Distinct(StringComparer.Ordinal).ToList();
        if (missingTypes.Count > 0)
        {
            findings.Add(new AnalysisFinding
            {
                Severity = FindingSeverity.Critical,
                Category = Localization.S("ca.catGameApi"),
                Title = Localization.SF("ca.missingTypesTitle", missingTypes.Count),
                Detail = Localization.S("ca.missingTypesDetail"),
                Evidence = string.Join(", ", missingTypes.Take(10)) + (missingTypes.Count > 10 ? " …" : string.Empty),
                Confidence = 0.85
            });
        }

        var missingMembers = dlls.SelectMany(d => d.MissingGameMembers).Distinct(StringComparer.Ordinal).ToList();
        if (missingMembers.Count > 0)
        {
            findings.Add(new AnalysisFinding
            {
                Severity = FindingSeverity.Critical,
                Category = Localization.S("ca.catGameApi"),
                Title = Localization.SF("ca.missingMembersTitle", missingMembers.Count),
                Detail = Localization.S("ca.missingMembersDetail"),
                Evidence = string.Join(", ", missingMembers.Take(10)) + (missingMembers.Count > 10 ? " …" : string.Empty),
                Confidence = 0.85
            });
        }

        var missingTargets = dlls.SelectMany(d => d.MissingHarmonyTargets).Distinct(StringComparer.Ordinal).ToList();
        var patchTargets = dlls.Sum(d => d.HarmonyPatchTargets.Count);
        if (patchTargets > 0)
            report.SignalsUsed.Add(Localization.SF("ca.sigHarmonyPatches", patchTargets));

        if (missingTargets.Count > 0)
        {
            findings.Add(new AnalysisFinding
            {
                Severity = FindingSeverity.High,
                Category = "Harmony",
                Title = Localization.SF("ca.missingTargetsTitle", missingTargets.Count),
                Detail = Localization.S("ca.missingTargetsDetail"),
                Evidence = string.Join(", ", missingTargets.Take(10)) + (missingTargets.Count > 10 ? " …" : string.Empty),
                Confidence = 0.75
            });
        }

        var referencesGame = dlls.Where(d => d.ReferencesGameAssembly).ToList();
        if (api is not null && referencesGame.Count > 0 && missingTypes.Count == 0 && missingMembers.Count == 0)
        {
            var typeRefs = referencesGame.Sum(d => d.GameTypesReferenced.Count);
            var memberRefs = referencesGame.Sum(d => d.GameMemberReferences);
            findings.Add(new AnalysisFinding
            {
                Severity = FindingSeverity.Info,
                Category = Localization.S("ca.catGameApi"),
                Title = Localization.S("ca.apiVerifiedTitle"),
                Detail = Localization.SF("ca.apiVerifiedDetail", typeRefs, memberRefs),
                Evidence = api.AssemblyVersion is "0.0.0.0" or "0.0.0"
                    ? Localization.S("ca.apiVersionUnspecified")
                    : $"Assembly-CSharp {api.AssemblyVersion}",
                Confidence = 0.8
            });
        }

        if (api is not null)
        {
            // 7 Days to Die runs on Unity/Mono: .NET Framework 4.x assemblies are the normal target for mods,
            // while assemblies built for .NET 5+/CoreCLR may fail to load. Only the latter is a real risk.
            var coreClrDlls = dlls.Where(d => d.Loadable && d.RuntimeFamily == ".NET / .NET Core").ToList();
            var frameworkDlls = dlls.Where(d => d.Loadable && d.RuntimeFamily == ".NET Framework").ToList();
            var families = dlls.Where(d => d.Loadable).Select(d => d.RuntimeFamily).Distinct().ToList();

            if (coreClrDlls.Count > 0 && api.RuntimeFamily != ".NET / .NET Core")
            {
                findings.Add(new AnalysisFinding
                {
                    Severity = FindingSeverity.High,
                    Category = Localization.S("ca.catRuntime"),
                    Title = Localization.S("ca.coreClrTitle"),
                    Detail = Localization.SF("ca.coreClrDetail", api.RuntimeFamily),
                    Evidence = string.Join(", ", coreClrDlls.Select(d => $"{d.FileName}: {d.TargetFramework ?? d.RuntimeFamily}")),
                    Confidence = 0.65
                });
            }
            else
            {
                findings.Add(new AnalysisFinding
                {
                    Severity = FindingSeverity.Info,
                    Category = Localization.S("ca.catRuntime"),
                    Title = Localization.S("ca.runtimeOkTitle"),
                    Detail = frameworkDlls.Count > 0
                        ? Localization.S("ca.runtimeFrameworkDetail")
                        : Localization.SF("ca.runtimeMixedDetail", string.Join(", ", families), api.RuntimeFamily),
                    Evidence = string.Join("; ", dlls.Where(d => d.TargetFramework is not null).Select(d => $"{d.FileName}: {d.TargetFramework}")),
                    Confidence = 0.6
                });
            }
        }

        var harmonyUsers = dlls.Where(d => d.ReferencesHarmony || d.UsesHarmonyAttributes).ToList();
        if (harmonyUsers.Count > 0)
        {
            var managedDir = game.ManagedAssemblyPath is null ? null : Path.GetDirectoryName(game.ManagedAssemblyPath);
            var gameHasHarmony = managedDir is not null && Directory.Exists(managedDir) &&
                                 Directory.GetFiles(managedDir, "*Harmony*.dll").Length > 0;

            // Since V1.0 Harmony ships as a regular mod (0_TFP_Harmony), not inside Managed.
            var harmonyMod = installed.FirstOrDefault(m => m.Content.DllFiles.Any(d =>
                Path.GetFileName(d).Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase)));
            var installedHarmony = harmonyMod is not null;

            if (!gameHasHarmony && !installedHarmony && !shipsHarmony)
            {
                findings.Add(new AnalysisFinding
                {
                    Severity = FindingSeverity.High,
                    Category = "Harmony",
                    Title = Localization.S("ca.harmonyMissingTitle"),
                    Detail = Localization.S("ca.harmonyMissingDetail"),
                    Evidence = string.Join(", ", harmonyUsers.Select(d => d.FileName)),
                    Confidence = 0.7
                });
            }
            else
            {
                findings.Add(new AnalysisFinding
                {
                    Severity = FindingSeverity.Info,
                    Category = "Harmony",
                    Title = Localization.S("ca.harmonyOkTitle"),
                    Detail = gameHasHarmony
                        ? Localization.S("ca.harmonyInManaged")
                        : installedHarmony
                            ? Localization.SF("ca.harmonyFromMod", harmonyMod!.DisplayTitle)
                            : Localization.S("ca.harmonyShipsOwn"),
                    Evidence = string.Join(", ", harmonyUsers.Select(d => d.FileName)),
                    Confidence = 0.75
                });
            }
        }

        if (shipsHarmony)
        {
            findings.Add(new AnalysisFinding
            {
                Severity = FindingSeverity.Medium,
                Category = "Harmony",
                Title = Localization.S("ca.shipsHarmonyTitle"),
                Detail = Localization.S("ca.shipsHarmonyDetail"),
                Evidence = Localization.S("ca.shipsHarmonyEvidence"),
                Confidence = 0.8
            });
        }
    }

    private static void AnalyzeConfigs(List<ConfigFileAnalysis> configs, List<AnalysisFinding> findings, CompatibilityReport report)
    {
        if (configs.Count == 0) return;

        var parsed = configs.Where(c => c.Parsed).ToList();
        var failed = configs.Where(c => !c.Parsed).ToList();

        foreach (var failure in failed.Take(5))
        {
            findings.Add(new AnalysisFinding
            {
                Severity = FindingSeverity.Low,
                Category = Localization.S("ca.catConfig"),
                Title = Localization.SF("ca.xmlNotParsedTitle", failure.RelativePath),
                Detail = Localization.S("ca.xmlNotParsedDetail"),
                Evidence = failure.Error ?? Localization.S("ca.noDetails"),
                Confidence = 0.8
            });
        }

        var replacements = parsed.Where(c => c.FullReplacement).ToList();
        if (replacements.Count > 0)
        {
            findings.Add(new AnalysisFinding
            {
                Severity = FindingSeverity.Medium,
                Category = Localization.S("ca.catConfig"),
                Title = Localization.SF("ca.fullReplacementTitle", replacements.Count),
                Detail = Localization.S("ca.fullReplacementDetail"),
                Evidence = string.Join(", ", replacements.Take(6).Select(r => r.RelativePath)) +
                           (replacements.Count > 6 ? " …" : string.Empty),
                Confidence = 0.9
            });
        }

        var patches = parsed.Where(c => c.UsesXPath).ToList();
        if (patches.Count > 0)
        {
            var operations = patches.Sum(p => p.XPathOperations);
            findings.Add(new AnalysisFinding
            {
                Severity = FindingSeverity.Info,
                Category = Localization.S("ca.catConfig"),
                Title = Localization.SF("ca.xpathTitle", operations, patches.Count),
                Detail = Localization.S("ca.xpathDetail"),
                Evidence = string.Join(", ", patches.Take(6).Select(p => p.RelativePath)),
                Confidence = 0.85
            });
        }

        var windows = parsed.SelectMany(c => c.WindowNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (windows.Count > 0)
            report.SignalsUsed.Add(Localization.SF("ca.sigWindows", windows.Count, string.Join(", ", windows.Take(8))));
    }

    private static void AnalyzeDeclaredVersion(ModPackage package, GameInstallInfo game, AppSettings settings, List<AnalysisFinding> findings)
    {
        var declared = package.Info.DeclaredGameVersion;
        var actual = game.VersionString ?? settings.GameVersionOverride;

        if (!string.IsNullOrWhiteSpace(declared) && !string.IsNullOrWhiteSpace(actual))
        {
            var declaredKey = NormalizeVersionToken(declared);
            var actualKey = NormalizeVersionToken(actual);

            if (declaredKey is not null && actualKey is not null && !declaredKey.Equals(actualKey, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new AnalysisFinding
                {
                    Severity = FindingSeverity.Medium,
                    Category = Localization.S("ca.catVersion"),
                    Title = Localization.SF("ca.declaredVersionTitle", declared),
                    Detail = Localization.SF("ca.declaredVersionDetail", actual),
                    Evidence = "ModInfo.xml → GameVersion/CompatibleVersion",
                    Confidence = 0.45
                });
            }
        }

        var versionText = package.Info.Version ?? string.Empty;
        if (package.Info.Format == ModInfoFormat.LegacyText || LegacyVersionRegex.IsMatch(versionText))
        {
            findings.Add(new AnalysisFinding
            {
                Severity = FindingSeverity.Low,
                Category = Localization.S("ca.catVersion"),
                Title = Localization.S("ca.legacyTitle"),
                Detail = Localization.S("ca.legacyDetail"),
                Evidence = Localization.SF("ca.legacyEvidence", package.Info.Format, package.VersionLabel),
                Confidence = 0.5
            });
        }
    }

    private static void AnalyzeDependencies(ModPackage package, IReadOnlyList<InstalledMod> installed, List<AnalysisFinding> findings)
    {
        foreach (var dependency in package.Info.Dependencies.Where(d => !d.Optional))
        {
            var satisfied = installed.Any(m =>
                m.Info.EffectiveName.Contains(dependency.Name, StringComparison.OrdinalIgnoreCase) ||
                dependency.Name.Contains(m.Info.EffectiveName, StringComparison.OrdinalIgnoreCase));

            findings.Add(new AnalysisFinding
            {
                Severity = satisfied ? FindingSeverity.Info : FindingSeverity.High,
                Category = Localization.S("ca.catDependencies"),
                Title = satisfied
                    ? Localization.SF("ca.depInstalledTitle", dependency.Name)
                    : Localization.SF("ca.depMissingTitle", dependency.Name),
                Detail = satisfied
                    ? Localization.S("ca.depInstalledDetail")
                    : Localization.S("ca.depMissingDetail"),
                Evidence = Localization.SF("ca.depEvidenceSource", dependency.Source) +
                           (dependency.Version is null ? string.Empty : Localization.SF("ca.depEvidenceVersion", dependency.Version)),
                Confidence = 0.85
            });
        }
    }

    private static void ComposeVerdict(
        CompatibilityReport report,
        List<ModAssemblyAnalysis> dlls,
        List<ConfigFileAnalysis> configs,
        GameApiIndex? api)
    {
        var critical = report.Findings.Count(f => f.Severity == FindingSeverity.Critical);
        var high = report.Findings.Count(f => f.Severity == FindingSeverity.High);

        var hasContent = dlls.Count > 0 || configs.Count > 0;
        var referencesGame = dlls.Any(d => d.ReferencesGameAssembly);

        if (!hasContent)
        {
            report.Verdict = CompatibilityVerdict.Unknown;
            report.Confidence = 0.2;
            report.Headline = Localization.S("ca.notEnoughData");
            report.Summary = Localization.S("ca.notEnoughDataSummary");
            report.NotVerified.Add(Localization.S("ca.nvContent"));
            return;
        }

        if (critical > 0)
        {
            report.Verdict = CompatibilityVerdict.LikelyIncompatible;
            report.Confidence = Math.Min(0.9, 0.78 + critical * 0.02);
            report.Headline = Localization.S("ca.verdictLikelyIncompatible");
            report.Summary = Localization.SF("ca.summaryLikelyIncompatible", critical);
        }
        else if (high > 0)
        {
            report.Verdict = CompatibilityVerdict.Risky;
            report.Confidence = 0.6;
            report.Headline = Localization.S("ca.verdictRisky");
            report.Summary = Localization.S("ca.summaryRisky");
        }
        else if (api is not null && referencesGame)
        {
            report.Verdict = CompatibilityVerdict.LikelyCompatible;
            report.Confidence = 0.8;
            report.Headline = Localization.S("ca.verdictLikelyCompatible");
            var versionLabel = api.AssemblyVersion is "0.0.0.0" or "0.0.0"
                ? Localization.S("ca.apiVersionUnspecified")
                : $"Assembly-CSharp {api.AssemblyVersion}";
            report.Summary = Localization.SF("ca.summaryCompatible", versionLabel);
        }
        else if (dlls.Count > 0)
        {
            report.Verdict = CompatibilityVerdict.Unknown;
            report.Confidence = 0.35;
            report.Headline = Localization.S("ca.verdictNeedsGame");
            report.Summary = Localization.S("ca.summaryNeedsGame");
        }
        else
        {
            report.Verdict = CompatibilityVerdict.LikelyCompatible;
            report.Confidence = 0.6;
            report.Headline = Localization.S("ca.verdictLikelyCompatible");
            report.Summary = Localization.S("ca.summaryXmlOnly");
        }

        report.NotVerified.Add(Localization.S("ca.nvLaunch"));
        report.NotVerified.Add(Localization.S("ca.nvHarmonyRuntime"));
        report.NotVerified.Add(Localization.S("ca.nvOtherMods"));
        report.SignalsUsed.Add(Localization.S("ca.sigMethod"));
    }

    private static string? NormalizeVersionToken(string value)
    {
        var match = Regex.Match(value, @"(?i)(v?\d+\.\d+(\.\d+)?)");
        if (match.Success) return match.Groups[1].Value.TrimStart('v', 'V');

        match = Regex.Match(value, @"(?i)\b(a\d{2}|alpha\s*\d+(\.\d+)?|b\d{2,3})\b");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant().Replace(" ", "") : null;
    }

    private static bool IsUnderRoot(string key, string root)
    {
        if (string.IsNullOrEmpty(root)) return true;
        return key.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConfigXml(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower.EndsWith(".xml") && (lower.Contains("/config/") || lower.StartsWith("config/"));
    }
}
