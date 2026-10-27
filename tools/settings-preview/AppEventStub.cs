namespace FxAuto.App;

/// <summary>Stand-in for FxAuto's AppEvent so App/Contracts.cs compiles without the coordinator's event file.</summary>
internal abstract record AppEvent(DateTimeOffset ArrivedAt);
