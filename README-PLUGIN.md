
# Jellyfin.Plugin.JellyTube — Scaffold

A minimal Jellyfin plugin that points Jellyfin at the `jellytube` backend.

## Files
- `Jellyfin.Plugin.JellyTube.csproj`
- `plugin.json`
- `Configuration/Plugin.cs`
- `Configuration/PluginConfiguration.cs`
- `Api/JellyTubeClient.cs`
- `MediaSources/JellyTubeMediaSourceProvider.cs`
- `Channels/JellyTubeChannel.cs` (skeleton)
- `Providers/JellyTubeMetadataProvider.cs` (skeleton)
- `Web/jellytube.html` (basic settings UI)

## Build
1. Ensure your Jellyfin server's `targetAbi` (e.g., 10.8.0).
2. Update the Jellyfin package versions in the `.csproj` to match your server.
3. Build:
   ```bash
   dotnet build -c Release
   ```
4. Deploy the resulting `Jellyfin.Plugin.JellyTube.dll` and `plugin.json` into your Jellyfin `plugins` directory, then restart Jellyfin.

## Next
- Implement `JellyTubeChannel` to surface configured sources from the backend.
- Implement `JellyTubeMetadataProvider` to fetch metadata from `/item/{id}`.
- Add a scheduled task to periodically sync sources.
