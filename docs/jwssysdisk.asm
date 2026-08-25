;
; z80dasm 1.1.6
; command line used to create the initial disassembly:
;  z80dasm --origin=0x6800 --labels --sym-input=thelabels.sym --output=jwssysdisk.asm --sym-output=jwsdossysdisk.sym --source JWSsysdiskwriter.bin
;
; make sure that
; z80asm jwssysdisk.asm -Lsysdisklabels.sym && diff a.bin JWSsysdiskwriter.bin && echo "all good"
;

MON_DSK_init:	equ 0x0ee2
MON_DSK_delay_342ms:	equ 0x0eff
MON_DSK_calibrate:	equ 0x0f08
MON_DSK_read_IO_status:	equ 0x0f62
MON_DSK_gotrack:	equ 0x0f7d
MON_DSK_motor_on:	equ 0x0f88
MON_DSK_send_command:	equ 0x0fa5
MON_dummy_handler:	equ 0x0fd6
BAS_CHAR_OUT:	equ 0x104a
BASIC_input_char:	equ 0x104d
MON_interrupt_ch0:	equ 0x6020
MON_interrupt_ch1:	equ 0x6022
dsk_transfer_adr:	equ 0x6070
dsk_transfer_cmd:	equ 0x6072
dsk_transfer_cmd_IOtype:	equ 0x6073
dsk_transfer_cmd_trk:	equ 0x6075
dsk_transfer_cmd_sec:	equ 0x6077
dsk_transfer_cmd_sectors_per_track:	equ 0x6079
dsk_seek_cmd_drive:	equ 0x607e
dsk_seek_cmd_track:	equ 0x607f
dsk_recall_cmd_drive:	equ 0x6082
chr_CRLF:	equ	01dh
chr_ENDTEXT:	equ 024h
STOP_key:	equ 003h
CTC_CH0:	equ 88h
CTC_CH1:	equ 89h
CTC_CH2:	equ 8ah
CTC_CH3:	equ 8bh
DSKCTRL:	equ 90h

	org	06800h

	ld hl,00fe8h				; copy upper 24 bytes from ROM to disk transfer addresses
								; 4 commands, necessary for disk access () are defined and filled
								; with the correct initial values here.
								; Command 1. Disk IO, used to read a full track
								; Command 2. Goto Track
								; Command 3. Drive Reset, inits drive and moves head to track 1
								; Command 4. Setup drive parameters
	ld de,dsk_transfer_adr		; destination 
	ld bc,24					; 24 bytes
	ldir						; and copy...

	call Get_TrackCount			; ask user for # of tracks (35, 40 or 80)
	cp STOP_key					; if STOP pressed: exit
	ret z
	call Get_SideCount			; ask user for # of sides (Single or Double)
	cp STOP_key					; if STOP pressed: exit
	ret z
	call Write_JWSDos			; The real job!
	ret							; and done

Write_JWSDos:
	call sub_6862h	
	call sub_68c6h
	ld hl,06a00h				; start of the JWSDos binary image
	ld (dsk_transfer_adr),hl	
	ld a,001h					; start with track 1
write_track_loop:
	push af											; save track #
	ld (dsk_transfer_cmd_trk),a						; prepare disk transfer command's track #
	dec a											; subtract one 
	ld (dsk_seek_cmd_track),a						; seek to track (zero-based!)
	inc a											; add one
	ld a,001h										; now overwrite with 1
	ld (dsk_transfer_cmd_sec),a						; start with sector 1 of the track 
	add a,00fh										; 1+15 = 16: # of sectors per track
	ld (dsk_transfer_cmd_sectors_per_track),a		; set in transfer command
	ld a,045h										; 45h = write , 42h = read
	ld (dsk_transfer_cmd_IOtype),a					; set write mode
	call MON_DSK_gotrack							; seek to sector 0
	ld a,(dsk_transfer_cmd_trk)						; are we writing part 2?
	cp 002h											; assume it is track 2, which needs
	ld e,8											; only 8 sectors 
	jr z,write_sectors							 			; it is so write them
	ld e,16											; track 1 has 16 sectors.
write_sectors:
	call sub_68ech									; write the sectors 
	ld (dsk_transfer_adr),hl						; HL points to next unwritten image byte
	call MON_DSK_read_IO_status						; get status from disk controller (stored at $6087)
	pop af											; get current track#
	inc a											; next track
	cp 003h											; if track# = 3 we're done
	jr nz,write_track_loop									; no, write next track (2)
	call Disk_off_kbd_on							; clean up and exit
	ret

sub_6862h:
	ld a,(l6936h+1)		;6862	3a 37 69 	: 7 i 
	ld (l6940h),a		;6865	32 40 69 	2 @ i 
	ld a,(l6936h)		;6868	3a 36 69 	: 6 i 
	ld (l693ah),a		;686b	32 3a 69 	2 : i 
	call sub_6879h		;686e	cd 79 68 	. y h 
	ld a,001h		;6871	3e 01 	> . 
	ld (068bfh),a		;6873	32 bf 68 	2 . h 
	jp Disk_off_kbd_on		;6876	c3 db 68 	. . h 

sub_6879h:
	ld a,(l6936h)		;6879	3a 36 69 	: 6 i 
	ld (dsk_seek_cmd_drive),a		;687c	32 7e 60 	2 ~ ` 
	res 4,a		;687f	cb a7 	. . 
	ld (dsk_recall_cmd_drive),a		;6881	32 82 60 	2 . ` 
	call sub_68c6h		;6884	cd c6 68 	. . h 
	call MON_DSK_calibrate		;6887	cd 08 0f 	. . . 
	call sub_691fh		;688a	cd 1f 69 	. . i 
	xor a			;688d	af 	. 
l688eh:
	push af			;688e	f5 	. 
	ld (0607fh),a		;688f	32 7f 60 	2  ` 
	inc a			;6892	3c 	< 
	ld (dsk_transfer_cmd_trk),a		;6893	32 75 60 	2 u ` 
	ld (0693fh),a		;6896	32 3f 69 	2 ? i 
	call 00f6ch		;6899	cd 6c 0f 	. l . 
	ld hl,l6936h+2		;689c	21 38 69 	! 8 i 
	call sub_6902h		;689f	cd 02 69 	. . i 
	ld e,010h		;68a2	1e 10 	. . 
	ld a,001h		;68a4	3e 01 	> . 
l68a6h:
	ld (l6941h),a		;68a6	32 41 69 	2 A i 
	ld b,004h		;68a9	06 04 	. . 
	ld hl,0693fh		;68ab	21 3f 69 	! ? i 
	call sub_6914h		;68ae	cd 14 69 	. . i 
	jr z,l68b9h		;68b1	28 06 	( . 
	ld a,(l6941h)		;68b3	3a 41 69 	: A i 
	inc a			;68b6	3c 	< 
	jr l68a6h		;68b7	18 ed 	. . 
l68b9h:
	call MON_DSK_read_IO_status		;68b9	cd 62 0f 	. b . 
	pop af			;68bc	f1 	. 
	inc a			;68bd	3c 	< 
	cp 001h		;68be	fe 01 	. . 
	jr nz,l688eh		;68c0	20 cc 	  . 
	call MON_DSK_calibrate		;68c2	cd 08 0f 	. . . 
	ret			;68c5	c9 	. 

sub_68c6h:
	di			;68c6	f3 	. 
	ld a,001h		;68c7	3e 01 	> . 
	out (CTC_CH0),a		;68c9	d3 88 	. . 
	out (CTC_CH1),a		;68cb	d3 89 	. . 
	out (CTC_CH2),a		;68cd	d3 8a 	. . 
	out (CTC_CH3),a		;68cf	d3 8b 	. . 
	call MON_DSK_init		;68d1	cd e2 0e 	. . . 
	call MON_DSK_motor_on		;68d4	cd 88 0f 	. . . 
	call MON_DSK_delay_342ms		;68d7	cd ff 0e 	. . . 
	ret			;68da	c9 	. 

Disk_off_kbd_on:
	ld a,003h			; Reset CTC channel 0 : timer/disk interrupt
	out (CTC_CH0),a		;  
	rst 20h				; enable keyboard interrupts
	xor a				; zero 
	out (DSKCTRL),a		; Disk motor off
	ei					; interrupts back on
	ret

l68e5h:
	call MON_DSK_read_IO_status		;68e5	cd 62 0f 	. b . 
	call Disk_off_kbd_on		;68e8	cd db 68 	. . h 
	ret			;68eb	c9 	. 

sub_68ech:
	call sub_691fh		;68ec	cd 1f 69 	. . i 
	ld hl,dsk_transfer_cmd		;68ef	21 72 60 	! r ` 
	call sub_6902h		;68f2	cd 02 69 	. . i 
	ld hl,(dsk_transfer_adr)		;68f5	2a 70 60 	* p ` 
l68f8h:
	call sub_6914h		;68f8	cd 14 69 	. . i 
	jr nz,l68f8h		;68fb	20 fb 	  . 
	ld a,00eh		;68fd	3e 0e 	> . 
	out (DSKCTRL),a		;68ff	d3 90 	. . 
	ret			;6901	c9 	. 

sub_6902h:
	call MON_DSK_send_command		;6902	cd a5 0f 	. . . 
	ld a,0c5h		;6905	3e c5 	> . 
	out (CTC_CH1),a		;6907	d3 89 	. . 
	ld a,001h		;6909	3e 01 	> . 
	out (CTC_CH1),a		;690b	d3 89 	. . 
	ld c,08dh		;690d	0e 8d 	. . 
	ld a,00dh		;690f	3e 0d 	> . 
	out (DSKCTRL),a		;6911	d3 90 	. . 
	ret			;6913	c9 	. 
sub_6914h:
	in a,(DSKCTRL)		;6914	db 90 	. . 
	rra			;6916	1f 	. 
	jr nc,sub_6914h		;6917	30 fb 	0 . 
	outi		;6919	ed a3 	. . 
	jr nz,sub_6914h		;691b	20 f7 	  . 
	dec e			;691d	1d 	. 
	ret			;691e	c9 	. 

sub_691fh:
	ld hl,MON_dummy_handler				;691f	21 d6 0f 	! . . 
	ld (MON_interrupt_ch0),hl			;6922	22 20 60 	"   ` 
	ld hl,l68e5h						;6925	21 e5 68 	! . h 
	ld (MON_interrupt_ch1),hl			;6928	22 22 60 	" " ` 
	ret									;692b	c9 	. 

PrintPrompt:
	ld a,(hl)				; get character to print 
	cp chr_ENDTEXT			; is it the end-marker? 
	ret z					; yes, done!
	call BAS_CHAR_OUT		; to screen 
	inc hl					; next character 
	jr PrintPrompt			; and loop
	
l6936h:
	db				01h, 00h, 06h
	db				4dh 
l693ah:
	db				01h, 01h, 10h 
	db				32h, 00h, 01h
l6940h:
	db				00h
	
l6941h:
	db				01h, 01h

SidesPromptText:
	db				chr_CRLF
	db				'1=SINGLE'
	db				chr_CRLF
	db				'2=DOUBLE  '
	db 				chr_ENDTEXT
TrackPromptText:
	db              chr_CRLF 
	db				'1=40 TRACKS'
	db				chr_CRLF
	db				'2=80 TRACKS'
	db              chr_CRLF
	db				'3=35 TRACKS  '
	db				chr_ENDTEXT
	
Get_SideCount:
	ld hl,SidesPromptText		; display side options
	call PrintPrompt
	call BASIC_input_char
	cp STOP_key					; exit on stop key
	ret z

	call BAS_CHAR_OUT			; show choice 
	cp '1'						; is it a 1?   
	jr nz,check_DS				; no, check for '2'

	ld a,'S'					; 1 = 'S'ingle Sided
	jr set_SS_DS_letter			; and Store

check_DS:
	cp '2'						; is it a 2?
	jr nz,Get_SideCount			; no, clear output and try

	ld a,'D'					; 2 = 'D'ouble Sided
set_SS_DS_letter:
	ld (079efh),a				; Store in system image
	ret

Get_TrackCount:
	ld hl,TrackPromptText		; Display track options
	call PrintPrompt
	call BASIC_input_char
	cp STOP_key
	ret z
	call BAS_CHAR_OUT			; display choice
	cp '3'						; 35 tracks? (option 3)
	jr nz,check_40_tracks

	ld a,35+1					; store # of tracks + 1 (internal value)
	ld (079ffh),a				; in system image
	ld a,'3'					; and store 35 as well 
								; the image contains '40' by default so 
								; 2 bytes need to be changed.
	ld (079f2h),a				; '3' in system image
	ld a,'5'					;
	ld (079f3h),a				; '5' in system image
	ret

check_40_tracks:
	cp '1'						; 40 tracks (option 1)
	jr nz,check_80_tracks

	ld a,40+1					; store # of tracks + 1 (internal value)
	ld (079ffh),a				; in system image
								; the image contains '40' by default so 
								; only the 1st byte needs to be changed to '4'
								; possible bug if a 35 track was inited first!

	ld a,'4'					
	ld (079f2h),a				; '4' in system image
	ret

check_80_tracks:
	cp '2'								; 80 tracks? (option 2)
	jr nz,Get_TrackCount				; no, clear output and try

	ld a,80+1					; store # of tracks + 1 (internal value)
	ld (079ffh),a				; in image 
								; the image contains '40' by default so 
								; only the 1st byte needs to be changed to '8'
								; possible bug if a 35 track was inited first!
	ld a,'8'
	ld (079f2h),a				; '8' in system image
	ret

; garbage left by obsolete versions of the code.
	ld d,c						; 'ld a' byte is missing, only the 51h left
	ld (077ffh),a		
	ld a,038h
	ld (077f2h),a
	ret

	ds			21 				; fill unused space with NOPs
