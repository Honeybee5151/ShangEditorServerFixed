using Shared.resources;
using Shared.terrain; //editor8182381_rename — was WorldServer.core.terrain (need Shared version for custom tile support)
using System.IO;

namespace WorldServer.core.worlds.impl
{
    public sealed class TestWorld : World
    {
        public TestWorld(GameServer gameServer, int id, WorldResource resource)
            : base(gameServer, id, resource)
        {
        }

        public override void Init()
        {
        }

        //editor8182381 — LoadJson with custom ground/object support for map testing
        public void LoadJson(string json)
        {
            var gameData = GameServer.Resources.GameData;
            var data = Json2Wmap.Convert(gameData, json, out var customGrounds, out var customObjects);

            //editor8182381 — Register custom objects so the map can reference them
            if (customObjects.Count > 0)
            {
                CustomObjectEntries = customObjects;
                gameData.RegisterCustomObjects(customObjects);
            }

            FromWorldMap(new MemoryStream(data));

            //editor8182381 — Store custom ground entries for client rendering
            if (customGrounds.Count > 0)
                CustomGroundEntries = customGrounds;

            PreCompressCustomChunks();
        }
    }
}
