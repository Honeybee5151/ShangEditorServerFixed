//editor8182381 — Sends custom ground pixel data to client for dungeon editor maps
using System;
using System.Collections.Generic;
using System.IO;
using Ionic.Zlib;
using NLog;
using Shared;
using Shared.resources;

namespace WorldServer.networking.packets.outgoing
{
    public class CustomGroundsMessage : OutgoingMessage
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>Pre-compressed blob (built once at world load). Preferred over Entries.</summary>
        public byte[] PreCompressed { get; set; }

        /// <summary>Fallback: raw entries to compress on-the-fly (only if PreCompressed is null).</summary>
        public List<CustomGroundEntry> Entries { get; set; }

        public override MessageId MessageId => MessageId.CUSTOM_GROUNDS;

        public override void Write(NetworkWriter wtr)
        {
            byte[] compressed;

            if (PreCompressed != null)
            {
                compressed = PreCompressed;
            }
            else
            {
                var entries = Entries ?? new List<CustomGroundEntry>();
                using (var ms = new MemoryStream())
                using (var bw = new NetworkWriter(ms))
                {
                    bw.Write(entries.Count);
                    foreach (var entry in entries)
                    {
                        bw.Write(entry.TypeCode);
                        var pixels = entry.DecodedPixels ?? new byte[192];
                        bw.Write(pixels, 0, Math.Min(pixels.Length, 192));
                        if (pixels.Length < 192)
                            bw.Write(new byte[192 - pixels.Length]);
                        // Flags byte: bit 0 = NoWalk
                        bw.Write((byte)(entry.NoWalk ? 1 : 0));
                        bw.Write((sbyte)entry.BlendPriority);
                        bw.Write(entry.Speed);
                        //editor8182381 — Write advanced ground properties (damage, sink, animate, push, slide)
                        bw.Write((short)entry.MinDamage);
                        bw.Write((short)entry.MaxDamage);
                        bw.Write(entry.Sink);
                        bw.Write((byte)entry.AnimateType);
                        bw.Write(entry.AnimateDx);
                        bw.Write(entry.AnimateDy);
                        bw.Write(entry.Push);
                        bw.Write(entry.SlideAmount);
                    }
                    bw.Flush();
                    compressed = ZlibStream.CompressBuffer(ms.ToArray());
                }
            }

            wtr.Write(compressed.Length);
            wtr.Write(compressed);
        }
    }
}
