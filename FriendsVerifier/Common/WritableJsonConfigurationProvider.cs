using Microsoft.Extensions.Configuration.Json;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FriendsVerifier.Common
{
    internal class WritableJsonConfigurationProvider(JsonConfigurationSource source) : JsonConfigurationProvider(source)
    {
        public override void Set(string key, string value)
        {
            JsonNode jsonNode;
            string filePath = Source.FileProvider.GetFileInfo(Source.Path).PhysicalPath;
            using (StreamReader file = new(filePath))
            {
                jsonNode = JsonNode.Parse(file.BaseStream);
            }
            jsonNode[key] = value;
            using StreamWriter stream = new(filePath);
            using Utf8JsonWriter writer = new(stream.BaseStream);
            jsonNode.WriteTo(writer);
            base.Set(key, value);
        }
    }
}
