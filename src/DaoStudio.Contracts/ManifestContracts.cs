using System.Text.Json.Serialization;

namespace DaoStudio.Contracts
{
    public sealed record LoaderSpec(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("officialReleaseUrl")] string OfficialReleaseUrl,
        [property: JsonPropertyName("sha256")] string Sha256);

    public sealed record FontAssetSpec(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("license")] string License,
        [property: JsonPropertyName("downloadUrl")] string DownloadUrl,
        [property: JsonPropertyName("sha256")] string Sha256);

    public sealed record GameProfileManifest(
        [property: JsonPropertyName("gameId")] string GameId,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("steamAppId")] string SteamAppId,
        [property: JsonPropertyName("executableName")] string ExecutableName,
        [property: JsonPropertyName("engine")] string Engine,
        [property: JsonPropertyName("loader")] LoaderSpec Loader,
        [property: JsonPropertyName("fontAsset")] FontAssetSpec FontAsset,
        [property: JsonPropertyName("pluginVersion")] string PluginVersion,
        [property: JsonPropertyName("glossaryVersion")] int GlossaryVersion);
}
