# APPENDIX I: THE VOLORG UTILITY

*DISK BASIC Reference Manual — App I, pages 1–4 (doc. 150)*

## Overview

The Volume Organization Utility supports the field-development of BASIC user programs. It resides on Disk P2306 or P2311 (PART 3). The utility is invoked by the following command:

```
RUN "B:VOLORG"
```

The following menu is shown:

```
P 2000     DISK   UTILITY

0 = LOAD AND RUN PROGRAM
1 = READ DISK DIRECTORY AND FREE SPACE
2 = COPY FILE
3 = COPY DISK
4 = DELETE FILE
5 = DELETE DISK
6 = RENAME FILE
7 = SET / RESET FILE PROTECT
8 = FILE SIZE
9 = COMPARE DISK
A = INPUTS FOR AUTO-LOAD
E = EXIT TO COMMAND MODE
R = RESET AFTER DISK CHANGE OR ERROR

SELECT
```

**Note** — Writing on a disk is only possible if the write-protect label is removed *and* if the disk does not contain a write-protected program file.

A short description of these functions follows.

---

## 0 — LOAD AND RUN PROGRAM

This function has the same effect as typing "E" (EXIT TO COMMAND MODE), followed by:

```
RUN "program name"
```

## 1 — READ DISK DIRECTORY AND FREE SPACE

This function displays the first 64 directory entries and the free space in kilobytes (= 1024 bytes). Typing a "P" causes the screen to be printed. Typing a "1" shows the other 64 directories alternately.

An asterisk behind the name shows that the file has been protected by VOLORG.

**Note** — For each 16k byte part of a file, a directory entry is created.

## 2 — COPY FILE

To copy a file from one disk to the same disk (a different file name must be used) or from one disk to another (the same or a different name may be used).

## 3 — COPY DISK

To copy a disk from Drive A to Drive B.

**Note** — The function can only be executed if the disk to be copied is not a system disk and if it does not contain a file protected by VOLORG.

## 4 — DELETE FILE

To delete a file.

## 5 — DELETE DISK

To delete the contents of a disk.

## 6 — RENAME FILE

To rename a file (filename + filename extension).

## 7 — SET / RESET FILE WRITE PROTECT

- To write-protect a program file. **This function causes the whole disk to be write-protected.**
- To write-protect a data file.
- To write-enable a data file.

## 8 — FILE SIZE

To show the length of a file in sectors (1 sector = 256 bytes).

## 9 — COMPARE DISK

This function compares the disks in Drive A and B and reports whether they are equal or not.

## A — INPUTS FOR AUTO-LOAD

With this option, the following parameters can be given to the system:

- The number of files to be processed simultaneously. It implies, also, the maximum file number used in BASIC programs.
- Runtime support. A "Y" means that during System Reset/Power On, the following subroutines are loaded:
  1. From address F800 upwards — FIELD ENTRY (P2306 and P2311).
  2. From address E500 to F7FF — KSAM (P2311 only).
- The program to be loaded and run automatically after System Reset/Power Up.

## E — EXIT TO COMMAND MODE

To leave the VOLORG program.

## R — RESET AFTER DISK CHANGE OR ERROR

After a disk change, it is necessary to tell the system to read the administration (directory) of the newly-inserted disk. After an error, VOLORG itself gives the RESET command (see also the RESET command in Chapter 3).
