namespace FxAuto.Core.Classification;

// Layer contract (plan.md "Layer contract", frozen in task 3). The record is `SourceClass`, never
// `Classification`, to avoid CS0118 with this namespace. Additions are fine; signature changes go to the lead.

/// <summary>Kind of a configured source.</summary>
public enum SourceKind { App, Game, Site, Browser }

/// <summary>One browser tab as read by UI Automation.</summary>
public sealed record BrowserTab(string Title, bool Audible, bool Selected);

/// <summary>What is known about a playing root process when it is classified. Tabs null = UIA unavailable.</summary>
public sealed record SourceContext(int RootPid, string? Exe, string? ExePath, string? Aumid,
    string? ActiveTitle, IReadOnlyList<BrowserTab>? Tabs);

/// <summary>Result of classifying a source (REQ-102): category id, matched source, site and how it matched.</summary>
public sealed record SourceClass(string Category, string? SourceId, string? Site, string How,
    bool IsGame, bool IsBrowser);

/// <summary>Classifies a source context into the fixed taxonomy.</summary>
public interface ISourceClassifier { SourceClass Classify(SourceContext ctx); }
