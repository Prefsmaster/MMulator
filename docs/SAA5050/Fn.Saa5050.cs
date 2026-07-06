using System;
using System.Diagnostics;
using PixelEngine;

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo

namespace FN.SAA5050
{
    public class Saa5050
    {
        private int previousColor;
        private bool holdOff;
        private int foregroundColor = 7;
        private int backgroundColor;
        private bool separatedGraphics;
        private bool doubleHeight;
        private bool oldDoubleHeight;
        private bool secondHalfOfDouble;
        private bool wasDoubleHeight;
        private bool graphicsMode;
        private bool flashing;
        private bool flashOn;
        private int flashTime;
        private byte heldChar;
        private bool holdChar;
        private readonly byte[] dataQueue = { 0,0,0,0 };
        private ushort scanLineCounter;
        private bool levelDEW;
        private bool levelLOSE;
        private bool levelCRS;

        private readonly uint[] normalGlyphs = new uint[96 * 20];  
        private readonly uint[] graphicsGlyphs = new uint[96 * 20]; 
        private readonly uint[] separatedGlyphs = new uint[96 * 20]; 
        private readonly uint[] colorTable = new uint[256];

        private uint[] nextGlyphs;
        private uint[] currentGlyphs;
        private uint[] heldGlyphs;

        // Pixel engine colors
        private readonly Pixel[] peColorTable = new Pixel[256];
        private readonly byte[] previousLineData = new byte[40];
        public Saa5050()
        {
            // start with 'normal' characters
            nextGlyphs = currentGlyphs = heldGlyphs = normalGlyphs;

            // build correct color table
            const float gamma = 1.0f / 2.2f;
            for (var color = 0; color < 256; color++)
            {
                var alpha = (color & 3) / 3.0;  // alpha = 0, 1/3, 2/3 or 1

                var fR = (color & 4) != 0 ? 1 : 0;
                var fG = (color & 8) != 0 ? 1 : 0;
                var fB = (color & 16) != 0 ? 1 : 0;
                var bR = (color & 32) != 0 ? 1 : 0;
                var bG = (color & 64) != 0 ? 1 : 0;
                var bB = (color & 128) != 0 ? 1 : 0;

                var blendR = (byte)(Math.Pow(fR * alpha + bR * (1.0 - alpha), gamma)*240);
                var blendG = (byte)(Math.Pow(fG * alpha + bG * (1.0 - alpha), gamma)*240);
                var blendB = (byte)(Math.Pow(fB * alpha + bB * (1.0 - alpha), gamma)*240);

                colorTable[color] = (uint)(0xFF<<24 | blendB << 16 | blendG << 8 | blendR);
                // also turn this into pixel engine compatible colors
                peColorTable[color] = Pixel.FromABgr(colorTable[color]);
            }

            // get character set data
            var characterData = SAA5050Data.makeChars();

            MakeHiresGlyphs(ref normalGlyphs);

            // Build connected graphics character set
            CreateGraphicsCharacterSet(false);

            MakeHiresGlyphs(ref graphicsGlyphs, false);

            CreateGraphicsCharacterSet(true);

            MakeHiresGlyphs(ref separatedGlyphs, false);

            void CreateGraphicsCharacterSet(bool separated)
            {
                for (var c = 0; c < 96; ++c)
                {
                    if ((c & 32) != 0) continue;

                    CreateGraphicsBlock(c, 0, 0, 3, 3, separated, (c & 1) == 1 ? 1 : 0);
                    CreateGraphicsBlock(c, 3, 0, 3, 3, separated, (c & 2) == 2 ? 1 : 0);
                    CreateGraphicsBlock(c, 0, 3, 3, 4, separated, (c & 4) == 4 ? 1 : 0);
                    CreateGraphicsBlock(c, 3, 3, 3, 4, separated, (c & 8) == 8 ? 1 : 0);
                    CreateGraphicsBlock(c, 0, 7, 3, 3, separated, (c & 16) == 16 ? 1 : 0);
                    CreateGraphicsBlock(c, 3, 7, 3, 3, separated, (c & 64) == 64 ? 1 : 0);
                }
            }

            ushort GetLoresGlyphPixelRow(int glyph, int row)
            {
                if (row < 0 || row >= 20) return 0;

                // start of row data in the array: each character has 60 bytes (6x10)
                // start of character is glyph*60. add 6 times desired row
                // (row can run from 0-19, data only has 0-9 so divide by 2)
                var index = glyph * 60 + (row / 2) * 6;

                // turn the 6 bits into 16 in reverse order, we output them reversed again!
                ushort result = 0;
                for (var bit = 0; bit < 12; bit+=2)
                {
                    result |= (ushort) ((characterData[index++] * 3) << bit);
                }
                return result;
            }

            ushort CombineRows(ushort rowA, ushort rowB)
            {
                // original:
                // return a | ((a >>> 1) & b & ~(b >>> 1)) | ((a << 1) & b & ~(b << 1));

                var part1 = rowA | ((rowA >> 1) & rowB & ~(rowB >> 1));
                var part2 = ((rowA << 1) & rowB & ~(rowB << 1));

                return (ushort)(part1 | part2);
            }

            void MakeHiresGlyphs(ref uint[] dest, bool createCharacters = true)
            {
                var index = 0;
                for (var c = 0; c < 96; ++c) {
                    for (var row = 0; row < 20; ++row) {
                        ushort data;
                        if (createCharacters || (c & 32)!=0) {
                            // in graphics mode, characters 64-95 ( @ A B C D...X Y Z <- -> , up and Pound) are also available and must be 
                            // rounded here as well...
                            data = CombineRows(GetLoresGlyphPixelRow(c, row), GetLoresGlyphPixelRow(c, row + ((row & 1)!=0 ? 1 : -1)));
                        } else {
                            data = GetLoresGlyphPixelRow(c, row);
                        }
                        dest[index++] = (uint)((data & 0x1) * 0x7) +           // 0000 0000 0000 0000 0000 0000 0000 0111
                                        (uint)((data & 0x2) * 0x14) +          // 0000 0000 0000 0000 0000 0000 0010 1000
                                        (uint)((data & 0x4) * 0x34) +          // 0000 0000 0000 0000 0000 0000 1101 0000
                                        (uint)((data & 0x8) * 0xE0) +          // 0000 0000 0000 0000 0000 0111 0000 0000
                                        (uint)((data & 0x10) * 0x280) +        // 0000 0000 0000 0000 0010 1000 0000 0000
                                        (uint)((data & 0x20) * 0x680) +        // 0000 0000 0000 0000 1101 0000 0000 0000
                                        (uint)((data & 0x40) * 0x1C00) +       // 0000 0000 0000 0111 0000 0000 0000 0000
                                        (uint)((data & 0x80) * 0x5000) +       // 0000 0000 0010 1000 0000 0000 0000 0000
                                        (uint)((data & 0x100) * 0xD000) +      // 0000 0000 1101 0000 0000 0000 0000 0000
                                        (uint)((data & 0x200) * 0x38000) +     // 0000 0111 0000 0000 0000 0000 0000 0000
                                        (uint)((data & 0x400) * 0xA0000) +     // 0010 1000 0000 0000 0000 0000 0000 0000
                                        (uint)((data & 0x800) * 0x1A0000);     // 1101 0000 0000 0000 0000 0000 0000 0000
                    }
                }
            }
            
            void CreateGraphicsBlock(int glyph, int x, int y, int w, int h, bool separated, int fill) {
                for (var yy = 0; yy < h; ++yy) {
                    for (var xx = 0; xx < w; ++xx)
                    {
                        var offset = glyph * 60 + (y + yy) * 6 + (x + xx);
                        var bit = (separated && (xx == 0 || yy == h - 1)) ?  0 : fill;
                        characterData[offset] = (byte)bit;
                    }
                }
            }
        }

        private void SetNextCharacters()
        {
            if (graphicsMode)
            {
                nextGlyphs = separatedGraphics ? separatedGlyphs : graphicsGlyphs;
            }
            else
            {
                nextGlyphs = normalGlyphs;
            }
        }

        private byte HandleControlCode(byte data)
        {
            holdOff = false;

            switch (data)
            {
                case 1:
                case 2:
                case 3:
                case 4:
                case 5:
                case 6:
                case 7:
                {
                    // character mode and color
                    graphicsMode = false;
                    foregroundColor = data;
                    SetNextCharacters();
                    break;
                }
                case 8:
                case 9:
                {
                    // flash on/off
                    flashing = data == 8;
                    break;
                }
                case 12:
                case 13:
                {
                    // double height on/off
                    doubleHeight = data == 13;
                    if (doubleHeight)
                        wasDoubleHeight = true;
                    break;
                }
                case 17:
                case 18:
                case 19:
                case 20:
                case 21:
                case 22:
                case 23:
                {
                    // graphics mode and color
                    graphicsMode = true;
                    foregroundColor = data & 0x07;
                    SetNextCharacters();
                    break;
                }
                case 24:
                {
                    // Conceal by rendering in BGColor
                    foregroundColor = previousColor = backgroundColor;
                    break;
                }
                case 25:
                case 26:
                {
                    separatedGraphics = data == 25;
                    SetNextCharacters();
                    break;
                }
                case 28:
                {
                    backgroundColor = 0;
                    break;
                }
                case 29:
                {
                    backgroundColor = foregroundColor;
                    break;
                }
                case 30:
                {
                    holdChar = true;
                    break;
                }
                case 31:
                {
                    holdOff = true;
                    break;
                }
            }
            if (holdChar && (doubleHeight == oldDoubleHeight))
            {
                data = heldChar;
                if (data >= 0x40 && data < 0x60)
                    data = 0x20;
                currentGlyphs = heldGlyphs;
            } else {
                heldChar = 0x20;
                data = 0x20;
            }
            return data;
        }

        public void FetchData(byte data)
        {
            dataQueue[0] = data;
        }

        public void SetDEW(bool level)
        {
            // The SAA5050 input pin "DEW" is used to track frames.
            var oldLevel = levelDEW;
            levelDEW = level;

            // Trigger on high -> low.
            if (!oldLevel || level) {
                return;
            }

            scanLineCounter = 0;
            secondHalfOfDouble = false;

            if (++flashTime == 48) flashTime = 0;
            flashOn = flashTime < 16;
        }

        public void SetLOSE(bool level)
        {
            // The SAA5050 input pin "LOSE" is used to track scan lines.
            var oldLevel = levelLOSE;
            levelLOSE = level;

            // Trigger on high -> low. This is probably what the hardware does as
            // we need to increment scan line at the end of the line, not the
            // beginning.
            if (!oldLevel || level) {
                return;
            }

            // reset registers back to default
            foregroundColor = 7;
            backgroundColor = 0;
            holdChar = false;
            heldChar = 0x20;
            nextGlyphs = heldGlyphs = normalGlyphs;
            flashing = false;
            separatedGraphics = false;
            graphicsMode = false;
            doubleHeight = false;

            scanLineCounter++;

            // Check for end of character row.
            if (scanLineCounter == 10) {
                scanLineCounter = 0;

                // when we rendered 2nd part of double height, reset to standard
                // if we were rendering top half, switch to second half
                // secondHalfOfDouble = secondHalfOfDouble ? false : wasDoubleHeight;

               secondHalfOfDouble = !secondHalfOfDouble && wasDoubleHeight;
            }

            wasDoubleHeight = false; // will be set again when ctrl character is found.
        }

        public void SetCRS(bool level)
        {
            // The SAA5050 input pin "CRS" is used to select between a
            // normal (even) scan line and a calculated smoothing (odd) scan line.
            levelCRS = level;
        }

        /// <summary>
        /// render pixels frame buffer style
        /// </summary>
        /// <param name="frameBuffer"></param>
        /// <param name="offset"></param>
        public void Render(uint[] frameBuffer, ulong offset)
        {
            var data = dataQueue[0];

            // we count 10 lines per character row, but use 20, interlaced
            var scanLine = (scanLineCounter << 1);
            // RA0 determines field 1/2
            if (levelCRS) {
                scanLine++;
            }

            oldDoubleHeight = doubleHeight;

            previousColor = foregroundColor;
            currentGlyphs = nextGlyphs;

            var prevFlash = flashing;

            if (data < 0x20) {
                data = HandleControlCode(data);
            }
            else
            {
                if (graphicsMode)
                {
                    heldChar = data;
                    heldGlyphs = currentGlyphs;
                }
            }

            // for double height, use first 5 lines twice for top, second 5 lines twice for bottom 
            if (oldDoubleHeight) {
                // scanLine is 0-18, so divide by 2 to process lines twice
                scanLine >>= 1;
                if (secondHalfOfDouble) {
                    scanLine += 10;
                }
            }

            // fetch bit pattern
            var chardef = currentGlyphs[(data - 0x20) * 20 + scanLine];

            // when we are rendering 2nd half of double height and double height is turned off: render nothing
            // also, when flash is turned on, and it was on, render nothing (background) 
            if (prevFlash && flashOn || secondHalfOfDouble && !doubleHeight) {
                var fillColor = colorTable[(this.backgroundColor & 7) << 5];
                for (var pixel = 0; pixel < 16; ++pixel) {
                    frameBuffer[offset++] = fillColor;
                }
            } else {
                var paletteIndex = (byte) (((backgroundColor & 7) << 5) | ((previousColor & 7) << 2));

                // optimization: we could unroll this loop

                // bits are stored mirrored, so we draw them backwards too
                // starting with the lower bits
                for (var pixel = 0; pixel < 16; ++pixel) {
                    frameBuffer[offset++] = colorTable[paletteIndex + (chardef & 3)];
                    chardef >>= 2;
                }
            }

            if (!holdOff) return;

            holdChar = false;
            heldChar = 0x20;
        }
        /// <summary>
        /// render pixels PixelEngine style
        /// </summary>
        /// <param name="peFrameBuffer"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        public void PERender(Sprite peFrameBuffer, int x, int y)
        {
            var data = dataQueue[0];

            var invert = (data & 0x80) != 0;

            data &= 0x7f;

            // we count 10 lines per character row, but use 20, interlaced
            var scanLine = (scanLineCounter << 1);
            // RA0 determines field 1/2
            if (levelCRS) {
                scanLine++;
            }

            oldDoubleHeight = doubleHeight;

            previousColor = foregroundColor;
            currentGlyphs = nextGlyphs;

            var prevFlash = flashing;

            if (secondHalfOfDouble) {
                data = previousLineData[x / 16];
            }

            previousLineData[x / 16] = data;

            if (data < 0x20) {
                data = HandleControlCode(data);
            }
            else
            {
                if (graphicsMode)
                {
                    heldChar = data;
                    heldGlyphs = currentGlyphs;
                }
            }

            // for double height, use first 5 lines twice for top, second 5 lines twice for bottom 
            if (oldDoubleHeight) {
                // scanLine is 0-18, so divide by 2 to process lines twice
                scanLine >>= 1;
                if (secondHalfOfDouble) {
                    scanLine += 10;
                }
            }

            // fetch bit pattern
            var chardef = currentGlyphs[(data - 0x20) * 20 + scanLine];

            // when we are rendering 2nd half of double height and double height is turned off: render nothing
            // also, when flash is turned on, and it was on, render nothing (background) 
            if (prevFlash && flashOn || secondHalfOfDouble && !doubleHeight) {
                var fillColor = peColorTable[(this.backgroundColor & 7) << (invert ? 2: 5)];
                for (var pixel = 0; pixel < 16; ++pixel) {
                    peFrameBuffer[x++,y] = fillColor;
                }
            } else {
                var paletteIndex = (byte) (((backgroundColor & 7) << (invert ? 2 : 5)) | ((previousColor & 7) << (invert?5:2)));

                // idea: see if we should unroll here?

                // bits are stored mirrored, so we draw them backwards too
                // starting with the lower bits
                for (var pixel = 0; pixel < 16; ++pixel) {
                    peFrameBuffer[x++,y] = peColorTable[paletteIndex + (chardef & 3)];
                    chardef >>= 2;
                }
            }

            if (holdOff) {
                holdChar = false;
                heldChar = 0x20;
            }
        }
    }
}
