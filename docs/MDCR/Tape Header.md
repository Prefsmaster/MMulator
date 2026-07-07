# Tape data block header format

| range (hex) | type  | Description |
| --------- | --------| --------- |
| 00-01 | word |    load address |
| 02-03 | word | Space occupied on tape can be (much) larger than the file itself. <br> This happens when a shorter file is written in place of a larger one.  |
| 04-05 | word | File size in bytes |
| 06-0D | 8 bytes | First 8 characters of the filename, padded with spaces. <br> BASIC NL only checks the first character! |
| 0E-10 | 3 bytes | 3 character file extension See list below for explanantion of extensions |
| 11 | byte | Creator ID of the application that wrote the file See Creator list below for definitions |
| 12 | byte | If Creator ID == 'P' (0x50) then this byte indicates language. <br> 'D' = German (Duits), 'S' = Swedish, 'U' = Dutch/English |
| 13-14 | word | Start address (execution address) for **Creator ID 'P' programs.** (no SLOT1 ROM required) |
| 15-16 | word | Load address (destination address) for **Creator ID 'P' programs.** |
| 17-1E | 8 bytes | Last 8 characters of the filename, padded with spaces. |
| 1F    | byte | Block counter for blocks to read, write or reverse |

File table format in Emulator:

16letterfilename.ext creatorcharacter filesize space occupied in blocks

Example:

`Header:` **`Filename         EXT Creator File size    Blocks`**<br>
`Line  :` **`Only13letters   .BAS       B    24.331        24`**

# Extensions
| Extension | Description |
| --------- | ----------- |
| BAS | BASIC - program |
| INT | Integer array |
| SNG | Single precision (float) array |
| DBL | Double precision (float) array |
| STR | String array |
| FAM | Familiegeheugen (database) file |
| ROM | Eprom-programmer file |
| BIS | Screen (image) made with BIS editor |
| PEP | Screen (image) made with Picture Editor Program |
| ASS | Assembly source made with Ron Eijnthoven's assembler |
| OBJ | Object code  made with Ron Eijnthoven's assembler |
| ASM | Assembly source in ASCII format |
| SIM | Logic simulator data |
| REL | Relocatable Z80 code, including symbol table |
| PGM | Auto startable program (see 'P') in the creator ID tabel |

# Creator IDs
| ID | Description |
| --------- | ----------- |
| '@' 0x40 | Picture by 'peter's picture program' |
| 'A' 0x41 | Familiegeheugen (database ROM) |
| 'B' 0x42 | BASIC |
| 'D' 0x44 | 24K Disk BASIC |
| 'F' 0x46 | FORTH |
| 'O' 0x4F | OTHER |
| 'P' 0x50 | Stand alone program that needs no ROM in Slot 1 |
| 'V' 0x56 | BIS Editor |
| 'W' 0x57 | Wordprocessor |

