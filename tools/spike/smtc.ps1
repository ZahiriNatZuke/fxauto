Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(3000) | Out-Null; $task.Result }
[void][Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
$sw = [Diagnostics.Stopwatch]::StartNew()
$mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
"RequestAsync ms=$($sw.ElapsedMilliseconds)"
$cur = $mgr.GetCurrentSession()
"Current: $($cur.SourceAppUserModelId)"
foreach ($s in $mgr.GetSessions()) {
  $p = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
  $pb = $s.GetPlaybackInfo()
  $tl = $s.GetTimelineProperties()
  "---- AUMID=$($s.SourceAppUserModelId)"
  "  Title='$($p.Title)' Artist='$($p.Artist)' Album='$($p.AlbumTitle)' AlbumArtist='$($p.AlbumArtist)' Subtitle='$($p.Subtitle)' TrackNo=$($p.TrackNumber) Genres=$($p.Genres -join ',') PlaybackType(props)=$($p.PlaybackType) Thumb=$([bool]$p.Thumbnail)"
  "  Status=$($pb.PlaybackStatus) PlaybackType(info)=$($pb.PlaybackType) Rate=$($pb.PlaybackRate) Shuffle=$($pb.IsShuffleActive) Repeat=$($pb.AutoRepeatMode)"
  "  Controls: Play=$($pb.Controls.IsPlayEnabled) Pause=$($pb.Controls.IsPauseEnabled) Next=$($pb.Controls.IsNextEnabled) Prev=$($pb.Controls.IsPreviousEnabled) Seek=$($pb.Controls.IsPlaybackPositionEnabled)"
  "  Timeline: Start=$($tl.StartTime) End=$($tl.EndTime) Pos=$($tl.Position) MinSeek=$($tl.MinSeekTime) MaxSeek=$($tl.MaxSeekTime) LastUpdated=$($tl.LastUpdatedTime)"
}
