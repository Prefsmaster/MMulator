using System;
using System.IO;

namespace fnP2000.Media
{
    public class MiniTape
    {
        // a philips data tape has room for:
        // START GAP
        // 40 data blocks
        // EOT GAP
        // running time of a tape is ~90 seconds, let's make that 91.
        // bits are written in 2 phases of 209 processor cycles each.
        // a tape can contain 91*2500000/209 = 1.088.516 phases
        // the tape format is digital, so we can store a phase in a bit.
        // for one side we need 1.088.516/8 = 136.065 bytes 

        private const int PhasesPerSide = 1088520;
        private const int BytesPerSide = PhasesPerSide/8;
 
        private readonly byte[][] data = new byte[2][];
        public int Position { get; private set; }
        public int Side { get; private set; }

        private readonly bool[] canWrite = { true, true };

        public MiniTape()
        {
            var rnd = new Random();
            // allocate room for 2 sides, and fill with garbage
            for (var side = 0; side < 2; side++)
            {
                data[side] = new byte[BytesPerSide];
                rnd.NextBytes(data[side]);
            }
            // Side A selected by default
            Side = 0;
            // the tape is not fully rewound by default
            Position = rnd.Next(PhasesPerSide/10);
        }

        public void LoadCasImage(byte[] casImage, int side = 0)
        {
            // The cas file format is pretty simple. it contains a sequence of blocks,
            // where a block consists of the 2 parts:
            // [0x00-0xff]   P2000 memory address 0x6000 - 0x60ff
            // 0x00-0x2f  Can be ignored (internal data like keyboard status etc.)
            // 0x30 - 0x4f = Block Header
            // 0x50 - 0xff Can be ignored (internal data like BasicROM variables)
            // [0x100-0x4ff] Data block (1024 bytes)
            // 
            // tape format:
            // BOT GAP        485 ms      5800 phases   725 bytes
            // BOB GAP        515 ms      6160 phases   770 bytes \
            // MARK             4 bytes     64 phases     8 bytes |
            // MARK GAP        85 ms      1024 phases   128 bytes  > BLOCK = 3258 bytes
            // DATA          1060 bytes  16960 phases  2120 bytes |
            // EOB GAP        155 ms      1856 phases   232 bytes /
            // EOT GAP       1800 ms     21536 phases  2692 bytes
            //
            // full tape = 725 + 40 * 3258 + 2692 = 133737 bytes.
            // Our tape has some slack with 136.065 bytes

            // first allocate a buffer
            data[side] = new byte[BytesPerSide];

            Position = 0;
            // skip BOT
            Position += 5800;

            byte[] marker = {};
            var block = new byte[1024+32];
            var blocks = casImage.Length / 1280;
            var index = 0;
#if DEBUG
            var headers = new byte[32*blocks];
#endif
            for (var b = 0; b<blocks;b++)
            {
                // copy header data
                Array.Copy(casImage,index+0x30,block,0,32);
#if DEBUG
                Array.Copy(casImage,index+0x30,headers,b*32,32);
#endif
                Array.Copy(casImage,index+0x100,block,32,1024);
                // skip BOB 
                Position += 6160;
                WriteData(marker);
                Position += 1024;
                WriteData(block);
                Position += 1856;

                index += 1024 + 256;
            } 
#if DEBUG
            // write headers to file
            var date = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.WriteAllBytes(@$"c:\temp\{date} headers.bin", headers);
#endif            
            // add EOT GAP not necessary. filled with 0's anyway!

            // Select correct side
            Side = side;
            // write protect on!
            canWrite[Side] = false;
            // the tape is not fully rewound by default
            Position = 100;
        }

        private void WriteData(byte[] bytes)
        {
            WriteByte(0xaa);

            ushort checksum = 0;
            foreach (var b in bytes)
            {
                WriteByte(b);
                checksum = UpdateCheckSum(checksum,b);
            }
            WriteByte((byte)(checksum&0xff));
            WriteByte((byte)((checksum>>8)&0xff));
            //WriteChecksum(checksum);

            WriteByte(0xaa);
        }

        private ushort UpdateCheckSum(ushort checksum, byte bits)
        {
            for (var c = 0; c < 8; c++)
            {
                var bit = (ushort)((bits & 0x01) != 0 ? 1 : 0);

                checksum ^= bit;
                // if resulting bit == 1 then XOR checksum with 0x4002
                if ((checksum & 0x01) != 0) checksum ^= 0x4002;
                // always rotate bits to the right.
                // lo bit moves into hi bit
                var hiBit = (checksum & 0x01)!=0 ? 0x8000:0;

                checksum = (ushort)(hiBit|checksum>>1);

                bits >>= 1;
            }
            return checksum;
        }

        private void WriteByte(byte bits)
        {
            for (var c = 0; c < 8; c++)
            {
                if ((bits & 0x01) != 0)
                {
                    // hi-lo transition
                    Write(true);
                    Position++;
                    //Write(false);
                    Position++;
                }
                else
                {
                    // lo-hi transition
                    //Write(false);
                    Position++;
                    Write(true);
                    Position++;
                }
                bits >>= 1;
            }
        }

        public void Save(string filename)
        {
            File.WriteAllBytes(filename, data[Side]);
        }

        public void Forward()
        {
            if (Position < PhasesPerSide - 1)
                Position++;
        }
        public void Reverse()
        {
            if (Position > 0)
                Position--;
        }

        public void Write(bool phase)
        {
            var byteOffset = Position / 8;
            var bitOffset = 7 - Position % 8;
            var bitMask = (byte)(1 << bitOffset);

            var pattern = data[Side][byteOffset];
            if (phase)
                pattern |= bitMask;
            else
                pattern &= (byte)~bitMask;
            data[Side][byteOffset] = pattern;
        }

        public bool Read()
        {
            var byteOffset = Position / 8;
            var bitOffset = 7 - Position % 8;
            var bitMask = (byte)(1 << bitOffset);

            var pattern = data[Side][byteOffset];

            return (pattern & bitMask) != 0;
        }

        public void SetSide(int side)
        {
            if (side == Side) return;

            Side = side;
            // when the tape is flipped, the Position is inverted too :-)
            Position = PhasesPerSide-1 - Position;
        }

        public bool Eot => Position == 0 || Position == PhasesPerSide - 1;
        public bool Protected => !canWrite[Side];
    }
}
