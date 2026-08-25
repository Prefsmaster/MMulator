; z80dasm 1.1.6
; command line: z80dasm --origin=0xE000 --labels --sym-input=MonSymbols.asm --output=PDOS.asm --sym-output=PDOS.sym --source PDOS.bin
; make sure that
; z80asm PDOS.asm -LPDOSlabels.sym && diff a.bin PDOS.bin && echo "all good"
; prints "all good"

	org	0e000h
CST_WCDON:	equ 0x0001
BIT_MOTON:	equ 0x0002
BIT_MOTWR:	equ 0x0003
CST_BOT:	equ 0x0004
FWD:	equ 0x0008
kb_scan_row:	equ 0x0101
l0303h:	equ 0x0303
Cartridge_ROM:	equ 0x1000
VIDEO_ram:	equ 0x5000
mon_status_io:	equ 0x6014
CTC_timer_disk:	equ 0x6020
CTC_communication:	equ 0x6022

timeout_irq_dest:		equ		0x6137
finished_irq_dest: equ	06130h

Disk_flag:	equ 0690fh


; if a CTC is present, the P2000 uses channel 3 for keyboard interrupt generation
CTC_CH0:                equ 088h         ; timer/disk interrupt
CTC_CH1:                equ 089h         ; disk not ready interrupt
CTC_CH2:                equ 08ah         ; communication (I/O) interrupt
CTC_CH3:                equ 08bh         ; keyboard interrupt

BANK_SWITCH:			equ 094h			; bank swith port
DISA:					equ 070h			; 1 = CPU prio, 0 = Video can make cpu wait.
;--------------------
; CTC control word break down
;bit    description
; 0     CTRLWRD     1 = this is a control word
; 1     RESET       1 = reset CTC
; 2     TCNEXT      1 = next word is a time constant
; 3     CLKSTRT     1 = start on next clock 0 = start immediately 
; 4     ACTTRG      1 = trigger on rising edge of clock
; 5     PRE256      1 = prescaler = 256, 0 = 16 
; 6     CNTMD       1 = Counter, 0 = timer
; 7     INTEN       1 = generate interrupt, 0 = don't generate

DSKIO1:                 equ 0x8C         ; INPUT status of FDC
;--------------------
;bit    description
; 7     RDY         1 = ready, 0 = not ready

DSKSTAT:                equ 0x8D         ; INPUT/OUTPUT 
;bit
; 2     REQ         1 = request status
; 3		SENSE INTERRUPT STATUS command

DSKCTRL:                equ 0x90
;--------------------
;bit    description
; 0     ENABLE      1 = read/write registers
; 1     Count       terminal count
; 2     RESET       1 = FDC reset
; 3     MOTOR       1 = on, 0 = off 
; 4     SELDIS      1 = Select disabled, 0 = normal, enabled
;                   Bit 4 only in use on P2C2 disk board

DC_BIT_ENABLE:			equ		0x01
DC_BIT_TERM_COUNT:		equ		0x02
DC_BIT_RESET:			equ		0x04
DC_BIT_MOTOR:			equ		0x08
DC_BIT_SELDIS:			equ		0x10

CPM_entry_point:
RAM_bank3:
	di						; interrupts off 	(f3) 
	im 2					; set interrupt mode 2
	ld (ix_pointer),ix		; save contents of ix
	ei						; interrupts needed for keyboard scanning

le008h:
	in a,(000h)				; key pressed?
	inc a					; ff (= no key pressed) becomes 0
	jr nz,le008h			; Wait for release

	di						; interrupts off
	ld (stack_store),sp		; save original stackpointer
	ld sp,0x61d0			; Disk opertion stack ix_pointer
	ld a,c					; get command (?)
	cp '2'					; '2'?
	jr nz,check_cmd_3			; noe018	20 22 	  " 

; handle command '2'
	ei
	xor a								; zero all flags
	ld (PDOS_flags),a
	call Init_drive
	call Sense_int_and_Specify
	jp exit_and_set_disk_off_timer

Init_drive:
	ld a,0x0C				; DC_BIT_MOTOR+DC_BIT_RESET
	out (DSKCTRL),a
	call delay				; give drive time to spin up
	ld a,(PDOS_flags)
	and 0bfh				; turn off bit 6 (0x40) 
	ld (PDOS_flags),a
	ld a,003h				; reset disk not ready interrupt
	out (CTC_CH1),a
	ret

check_cmd_3:
	cp '3'
	jr nz,le04eh		;e03e	20 0e 	  . 

; handle command '3'
	call motor_off		;e040	cd 46 e0 	. F . 
	jp exit_and_set_disk_off_timer		;e043	c3 8e e2 	. . . 

motor_off:
	ld a,DC_BIT_RESET					; 00000100 = FDC reset, motor off
	out (DSKCTRL),a
	call set_bit6_enable_ints
	ret
										;e04d	c9 	. 
le04eh:
	ld (lf530h),iy		;e04e	fd 22 30 f5 	. " 0 . 
	cp 039h		;e052	fe 39 	. 9 
	jr nz,le059h		;e054	20 03 	  . 
	ld (lf589h),hl		;e056	22 89 f5 	" . . 
le059h:
	bit 7,a		;e059	cb 7f 	.  
	jr z,le07dh		;e05b	28 20 	(   
	ld (lf589h),hl		;e05d	22 89 f5 	" . . 
	ld hl,lf58bh		;e060	21 8b f5 	! . . 
	set 0,(hl)		;e063	cb c6 	. . 
	ld hl,PDOS_flags		;e065	21 24 f5 	! $ . 
	set 7,(hl)		;e068	cb fe 	. . 
	push af			;e06a	f5 	. 
	call sub_eb0ch		;e06b	cd 0c eb 	. . . 
	ld hl,05002h		;e06e	21 02 50 	! . P 
	ld de,lf694h		;e071	11 94 f6 	. . . 
	call copy_8_bytes		;e074	cd d4 e5 	. . . 
	ld de,lf693h		;e077	11 93 f6 	. . . 
	xor a			;e07a	af 	. 
	ld (de),a			;e07b	12 	. 
	pop af			;e07c	f1 	. 
le07dh:
	ld (lf52ch),de		;e07d	ed 53 2c f5 	. S , . 
	push af			;e081	f5 	. 
	xor a			;e082	af 	. 
	ld (Disk_error_code),a		;e083	32 1e f5 	2 . . 
	call 00023h		;e086	cd 23 00 	. # . 
	ld hl,PDOS_flags		;e089	21 24 f5 	! $ . 
	bit 6,(hl)		;e08c	cb 76 	. v 
	call nz,Init_drive
	call Sense_int_and_Specify
	call plot_activity_D
	pop af			;e097	f1 	. 
	res 7,a		;e098	cb bf 	. . 
	ld (Retry_counter+1),a		;e09a	32 1d f5 	2 . . 
	ld hl,PDOS_flags		;e09d	21 24 f5 	! $ . 
	cp 02bh		;e0a0	fe 2b 	. + 
	jr z,le0a6h		;e0a2	28 02 	( . 
	res 4,(hl)		;e0a4	cb a6 	. . 
le0a6h:
	cp 010h		;e0a6	fe 10 	. . 
	jp z,le1a1h		;e0a8	ca a1 e1 	. . . 
	cp 00dh		;e0ab	fe 0d 	. . 
	jr nz,le0b4h		;e0ad	20 05 	  . 
	call sub_e2c8h		;e0af	cd c8 e2 	. . . 
	jr le0f1h		;e0b2	18 3d 	. = 
le0b4h:
	cp 00eh		;e0b4	fe 0e 	. . 
	jp z,le149h		;e0b6	ca 49 e1 	. I . 
	cp 00fh		;e0b9	fe 0f 	. . 
	jr nz,le0f3h		;e0bb	20 36 	  6 
	bit 7,(hl)		;e0bd	cb 7e 	. ~ 
	jr z,le0c4h		;e0bf	28 03 	( . 
	call sub_e2c8h		;e0c1	cd c8 e2 	. . . 
le0c4h:
	call sub_e52eh		;e0c4	cd 2e e5 	. . . 
	call read_byte_bank_0-0x88c5		;e0c7	cd 3e 61 	. > a 
	ld a,c			;e0ca	79 	y 
	cp 000h		;e0cb	fe 00 	. . 
	jr z,le0f1h		;e0cd	28 22 	( " 
	ld hl,060d0h		;e0cf	21 d0 60 	! . ` 
	bit 0,(hl)		;e0d2	cb 46 	. F 
	jr nz,le0f1h		;e0d4	20 1b 	  . 
	ld hl,(lf52ch)		;e0d6	2a 2c f5 	* , . 
	ld bc,00009h		;e0d9	01 09 00 	. . . 
	add hl,bc			;e0dc	09 	. 
	ld a,(hl)			;e0dd	7e 	~ 
	cp 042h		;e0de	fe 42 	. B 
	jr nz,le0f1h		;e0e0	20 0f 	  . 
	inc hl			;e0e2	23 	# 
	ld a,(hl)			;e0e3	7e 	~ 
	cp 041h		;e0e4	fe 41 	. A 
	jr nz,le0f1h		;e0e6	20 09 	  . 
	inc hl			;e0e8	23 	# 
	ld a,(hl)			;e0e9	7e 	~ 
	cp 053h		;e0ea	fe 53 	. S 
	jr nz,le0f1h		;e0ec	20 03 	  . 
	call sub_e943h		;e0ee	cd 43 e9 	. C . 
le0f1h:
	jr le14ch		;e0f1	18 59 	. Y 
le0f3h:
	cp 017h		;e0f3	fe 17 	. . 
	jr nz,le0fch		;e0f5	20 05 	  . 
	call sub_e5f7h		;e0f7	cd f7 e5 	. . . 
	jr le14ch		;e0fa	18 50 	. P 
le0fch:
	cp 011h		;e0fc	fe 11 	. . 
	jr nz,le10ah		;e0fe	20 0a 	  . 
	call sub_e51fh		;e100	cd 1f e5 	. . . 
	ld (lf587h),hl		;e103	22 87 f5 	" . . 
	ld a,011h		;e106	3e 11 	> . 
	jr le149h		;e108	18 3f 	. ? 
le10ah:
	cp 012h		;e10a	fe 12 	. . 
	jr z,le149h		;e10c	28 3b 	( ; 
	cp 014h		;e10e	fe 14 	. . 
	jr z,le149h		;e110	28 37 	( 7 
	cp 015h		;e112	fe 15 	. . 
	jr nz,le11bh		;e114	20 05 	  . 
	call sub_e2ebh		;e116	cd eb e2 	. . . 
	jr le14ch		;e119	18 31 	. 1 
le11bh:
	cp 013h		;e11b	fe 13 	. . 
	jr nz,le124h		;e11d	20 05 	  . 
	call sub_e31bh		;e11f	cd 1b e3 	. . . 
	jr le14ch		;e122	18 28 	. ( 
le124h:
	cp 016h		;e124	fe 16 	. . 
	jr nz,le12dh		;e126	20 05 	  . 
	call sub_e2dch		;e128	cd dc e2 	. . . 
	jr le14ch		;e12b	18 1f 	. . 
le12dh:
	cp 01ah		;e12d	fe 1a 	. . 
	jr z,le149h		;e12f	28 18 	( . 
	cp 018h		;e131	fe 18 	. . 
	jr z,le149h		;e133	28 14 	( . 
	cp 019h		;e135	fe 19 	. . 
	jr z,le149h		;e137	28 10 	( . 
	cp 01bh		;e139	fe 1b 	. . 
	jr z,le149h		;e13b	28 0c 	( . 
	cp 01ch		;e13d	fe 1c 	. . 
	jr z,le149h		;e13f	28 08 	( . 
	cp 01dh		;e141	fe 1d 	. . 
	jr z,le149h		;e143	28 04 	( . 
	cp 01eh		;e145	fe 1e 	. . 
	jr nz,le14fh		;e147	20 06 	  . 
le149h:
	call sub_e705h		;e149	cd 05 e7 	. . . 
le14ch:
	jp exit_and_set_disk_off_timer		;e14c	c3 8e e2 	. . . 
le14fh:
	call sub_e2c8h		;e14f	cd c8 e2 	. . . 
	ld a,(Retry_counter+1)		;e152	3a 1d f5 	: . . 
	cp 028h		;e155	fe 28 	. ( 
	jr nz,le15eh		;e157	20 05 	  . 
	call sub_e74ch		;e159	cd 4c e7 	. L . 
	jr le1a4h		;e15c	18 46 	. F 
le15eh:
	cp 029h		;e15e	fe 29 	. ) 
	jr nz,le184h		;e160	20 22 	  " 
	call sub_e67dh		;e162	cd 7d e6 	. } . 
le165h:
	call sub_e2cch		;e165	cd cc e2 	. . . 
	jr c,le1a4h		;e168	38 3a 	8 : 
le16ah:
	call sub_e5e0h		;e16a	cd e0 e5 	. . . 
	jr nz,le16ah		;e16d	20 fb 	  . 
	cp 040h		;e16f	fe 40 	. @ 
	jr nz,le1a4h		;e171	20 31 	  1 
	ld hl,0f69fh		;e173	21 9f f6 	! . . 
	inc (hl)			;e176	34 	4 
	xor a			;e177	af 	. 
	ld (lf6b3h),a		;e178	32 b3 f6 	2 . . 
	jr le165h		;e17b	18 e8 	. . 
	ld a,007h		;e17d	3e 07 	> . 
	call Set_error_and_Not_ready_message		;e17f	cd 34 e3 	. 4 . 
	jr le1a4h		;e182	18 20 	.   
le184h:
	cp 02ah		;e184	fe 2a 	. * 
	jr nz,le1a7h		;e186	20 1f 	  . 
	call sub_e67dh		;e188	cd 7d e6 	. } . 
	call sub_e31bh		;e18b	cd 1b e3 	. . . 
	call sub_e2dch		;e18e	cd dc e2 	. . . 
	jr c,le1a4h		;e191	38 11 	8 . 
le193h:
	call sub_e2ebh		;e193	cd eb e2 	. . . 
	jr c,le1a4h		;e196	38 0c 	8 . 
	call sub_e6d3h		;e198	cd d3 e6 	. . . 
	jr z,le1a1h		;e19b	28 04 	( . 
	jr c,le1a1h		;e19d	38 02 	8 . 
	jr le193h		;e19f	18 f2 	. . 
le1a1h:
	call sub_e329h		;e1a1	cd 29 e3 	. ) . 
le1a4h:
	jp exit_and_set_disk_off_timer		;e1a4	c3 8e e2 	. . . 
le1a7h:
	cp 02bh		;e1a7	fe 2b 	. + 
	jr nz,le1b7h		;e1a9	20 0c 	  . 
	call sub_e6e8h		;e1ab	cd e8 e6 	. . . 
	call sub_e612h		;e1ae	cd 12 e6 	. . . 
	call print_header_and_footer		;e1b1	cd dc ea 	. . . 
	jp le296h		;e1b4	c3 96 e2 	. . . 
le1b7h:
	cp 036h		;e1b7	fe 36 	. 6 
	jr nz,le1fch		;e1b9	20 41 	  A 
	ld hl,0x0000		;e1bb	21 00 00 	! . . 
	ld (lf57dh),hl		;e1be	22 7d f5 	" } . 
	call sub_e67dh		;e1c1	cd 7d e6 	. } . 
le1c4h:
	call sub_e2cch		;e1c4	cd cc e2 	. . . 
	ld a,000h		;e1c7	3e 00 	> . 
	jr c,le1f0h		;e1c9	38 25 	8 % 
	ld hl,(lf52ch)		;e1cb	2a 2c f5 	* , . 
	ld de,0000fh		;e1ce	11 0f 00 	. . . 
	add hl,de			;e1d1	19 	. 
	ld a,(hl)			;e1d2	7e 	~ 
	cp 040h		;e1d3	fe 40 	. @ 
	jr nz,le1f0h		;e1d5	20 19 	  . 
	dec hl			;e1d7	2b 	+ 
	dec hl			;e1d8	2b 	+ 
	dec hl			;e1d9	2b 	+ 
	inc (hl)			;e1da	34 	4 
	inc hl			;e1db	23 	# 
	ld bc,00013h		;e1dc	01 13 00 	. . . 
	push af			;e1df	f5 	. 
	call sub_e68ch		;e1e0	cd 8c e6 	. . . 
	pop af			;e1e3	f1 	. 
	ld hl,(lf57dh)		;e1e4	2a 7d f5 	* } . 
	ld c,a			;e1e7	4f 	O 
	ld b,000h		;e1e8	06 00 	. . 
	add hl,bc			;e1ea	09 	. 
	ld (lf57dh),hl		;e1eb	22 7d f5 	" } . 
	jr le1c4h		;e1ee	18 d4 	. . 
le1f0h:
	ld hl,(lf57dh)		;e1f0	2a 7d f5 	* } . 
	ld c,a			;e1f3	4f 	O 
	ld b,000h		;e1f4	06 00 	. . 
	add hl,bc			;e1f6	09 	. 
	ld (lf57bh),hl		;e1f7	22 7b f5 	" { . 
	jr le240h		;e1fa	18 44 	. D 
le1fch:
	cp 02fh		;e1fc	fe 2f 	. / 
	jr nz,le205h		;e1fe	20 05 	  . 
	call sub_e3c7h		;e200	cd c7 e3 	. . . 
	jr le240h		;e203	18 3b 	. ; 
le205h:
	cp 02eh		;e205	fe 2e 	. . 
	jr nz,le20eh		;e207	20 05 	  . 
	call sub_e33dh		;e209	cd 3d e3 	. = . 
	jr le240h		;e20c	18 32 	. 2 
le20eh:
	cp 037h		;e20e	fe 37 	. 7 
	jr nz,le21ah		;e210	20 08 	  . 
	call sub_e50dh		;e212	cd 0d e5 	. . . 
	call sub_e8b3h		;e215	cd b3 e8 	. . . 
	jr le240h		;e218	18 26 	. & 
le21ah:
	cp 038h		;e21a	fe 38 	. 8 
	jr nz,le229h		;e21c	20 0b 	  . 
	call sub_e50dh		;e21e	cd 0d e5 	. . . 
	ld (lf56dh),a		;e221	32 6d f5 	2 m . 
	call sub_e84fh		;e224	cd 4f e8 	. O . 
	jr le240h		;e227	18 17 	. . 
le229h:
	cp 039h		;e229	fe 39 	. 9 
	jr nz,le242h		;e22b	20 15 	  . 
	ld hl,(ix_pointer)		;e22d	2a 32 f5 	* 2 . 
	ld a,h			;e230	7c 	| 
	push hl			;e231	e5 	. 
	call sub_eb23h		;e232	cd 23 eb 	. # . 
	pop hl			;e235	e1 	. 
	ld a,l			;e236	7d 	} 
	ld (RW_cmd_track),a		;e237	32 39 f5 	2 9 . 
	ld hl,(lf589h)		;e23a	2a 89 f5 	* . . 
	call sub_e496h		;e23d	cd 96 e4 	. . . 
le240h:
	jr exit_and_set_disk_off_timer		;e240	18 4c 	. L 
le242h:
	cp 03bh		;e242	fe 3b 	. ; 
	jr nz,le250h		;e244	20 0a 	  . 
	ld a,039h		;e246	3e 39 	> 9 
	ld (Retry_counter+1),a		;e248	32 1d f5 	2 . . 
	call sub_e4a5h		;e24b	cd a5 e4 	. . . 
	jr exit_and_set_disk_off_timer		;e24e	18 3e 	. > 
le250h:
	cp 03ah		;e250	fe 3a 	. : 
	jr nz,le272h		;e252	20 1e 	  . 
	call sub_e52bh		;e254	cd 2b e5 	. + . 
	set 1,(hl)		;e257	cb ce 	. . 
	call sub_e329h		;e259	cd 29 e3 	. ) . 
	call sub_e526h		;e25c	cd 26 e5 	. & . 
	push hl			;e25f	e5 	. 
	call sub_e53ch		;e260	cd 3c e5 	. < . 
	call sub_e79ah		;e263	cd 9a e7 	. . . 
	ld a,0f3h		;e266	3e f3 	> . 
	pop hl			;e268	e1 	. 
	ld (hl),a			;e269	77 	w 
	call sub_e53ch		;e26a	cd 3c e5 	. < . 
	call sub_e847h		;e26d	cd 47 e8 	. G . 
	jr exit_and_set_disk_off_timer		;e270	18 1c 	. . 
le272h:
	cp 034h		;e272	fe 34 	. 4 
	jr nz,le27dh		;e274	20 07 	  . 
	call sub_e52bh		;e276	cd 2b e5 	. + . 
	set 0,(hl)		;e279	cb c6 	. . 
	jr le286h		;e27b	18 09 	. . 
le27dh:
	cp 035h		;e27d	fe 35 	. 5 
	jr nz,le289h		;e27f	20 08 	  . 
	call sub_e52bh		;e281	cd 2b e5 	. + . 
	res 0,(hl)		;e284	cb 86 	. . 
le286h:
	jp le1a1h		;e286	c3 a1 e1 	. . . 
le289h:
	cp 030h		;e289	fe 30 	. 0 
	call z,sub_e612h		;e28b	cc 12 e6 	. . . 


exit_and_set_disk_off_timer:
	ld a,(PDOS_flags)
	and 040h				; clear all flags except # 6
	ld (PDOS_flags),a		;e293	32 24 f5 	2 $ . 

; 0     CTRLWRD     1 = this is a control word
; 1     RESET       1 = reset CTC
; 2     TCNEXT      1 = next word is a time constant
; 3     CLKSTRT     1 = start on next clock 0 = start immediately 
; 4     ACTTRG      1 = trigger on rising edge of clock
; 5     PRE256      1 = prescaler = 256, 0 = 16 
; 6     CNTMD       1 = Counter, 0 = timer
; 7     INTEN       1 = generate interrupt, 0 = don't generate


le296h:
	ld a,003h				; reset  
	out (CTC_CH0),a			; Disk/timer interrupt
	out (CTC_CH1),a			; and Disk not ready interrupt 
	ld a,0a5h				; = 10100101 =  CTRLWRD + TIMECONSTANT follows + Prescale 256 + Generate interupt
	out (CTC_CH1),a			; 
	ld a,0ffh				;
	out (CTC_CH1),a			; timeconstant = 255, sets disk time out interrupt after 65536 cycles = ~0.02 sec

	ld hl,irq_code			; copy interrupt handler routine
	ld de,timeout_irq_dest			; to basic memory
	ld bc,00032h			; 32 = 50 bytes
	ldir
	rst 20h					; call init keyscan 

	xor a					; Video chip has priority
	out (DISA),a

	call clear_activity_letter

	ld hl,(lf57bh)			; get ???
	ld a,l					; lo byte in A
	ld b,h					; hi byte in B 
	ld d,a					; save lo byte
	ld a,(Disk_error_code)			; get ???
	or a					; set flags
	ld c,a					; save in C 
	ld a,d					; lo byte back in A
	ld sp,(stack_store)		; restore stack pointer
	ei
	ret						; Z is set if no error


sub_e2c8h:
	ld c,00dh		;e2c8	0e 0d 	. . 
	jr le325h		;e2ca	18 59 	. Y 
sub_e2cch:
	ld c,00fh		;e2cc	0e 0f 	. . 
	call sub_e706h		;e2ce	cd 06 e7 	. . . 
	cp 0ffh		;e2d1	fe ff 	. . 
	jr nz,le2e5h		;e2d3	20 10 	  . 
	ld a,001h		;e2d5	3e 01 	> . 
	ld (Disk_error_code),a		;e2d7	32 1e f5 	2 . . 
	scf			;e2da	37 	7 
	ret			;e2db	c9 	. 
sub_e2dch:
	ld c,016h		;e2dc	0e 16 	. . 
	call sub_e706h		;e2de	cd 06 e7 	. . . 
	cp 0ffh		;e2e1	fe ff 	. . 
	jr z,le2f4h		;e2e3	28 0f 	( . 
le2e5h:
	xor a			;e2e5	af 	. 
	ret			;e2e6	c9 	. 
sub_e2e7h:
	ld c,014h		;e2e7	0e 14 	. . 
	jr le325h		;e2e9	18 3a 	. : 
sub_e2ebh:
	ld c,015h		;e2eb	0e 15 	. . 
	call sub_e706h		;e2ed	cd 06 e7 	. . . 
	cp 0ffh		;e2f0	fe ff 	. . 
	jr nz,le2fdh		;e2f2	20 09 	  . 
le2f4h:
	ld a,003h		;e2f4	3e 03 	> . 
	ld (Disk_error_code),a		;e2f6	32 1e f5 	2 . . 
	ld b,001h		;e2f9	06 01 	. . 
	jr le339h		;e2fb	18 3c 	. < 
le2fdh:
	cp 001h		;e2fd	fe 01 	. . 
	jr nz,le305h		;e2ff	20 04 	  . 
	ld a,005h		;e301	3e 05 	> . 
	jr Set_error_and_Not_ready_message		;e303	18 2f 	. / 
le305h:
	cp 002h		;e305	fe 02 	. . 
	jr nz,le2e5h		;e307	20 dc 	  . 
	call sub_e329h		;e309	cd 29 e3 	. ) . 
	ld a,004h		;e30c	3e 04 	> . 
	ld (Disk_error_code),a		;e30e	32 1e f5 	2 . . 
	ld hl,BIT_MOTON		;e311	21 02 00 	! . . 
	ld (lf57bh),hl		;e314	22 7b f5 	" { . 
	ld b,002h		;e317	06 02 	. . 
	jr le339h		;e319	18 1e 	. . 
sub_e31bh:
	call sub_e56bh		;e31b	cd 6b e5 	. k . 
	jr z,le323h		;e31e	28 03 	( . 
	call sub_e2c8h		;e320	cd c8 e2 	. . . 
le323h:
	ld c,013h		;e323	0e 13 	. . 
le325h:
	call sub_e706h		;e325	cd 06 e7 	. . . 
	ret			;e328	c9 	. 
sub_e329h:
	ld c,010h		;e329	0e 10 	. . 
	call sub_e706h		;e32b	cd 06 e7 	. . . 
	cp 0ffh		;e32e	fe ff 	. . 
	jr nz,le2e5h		;e330	20 b3 	  . 
	ld a,006h		;e332	3e 06 	> . 

Set_error_and_Not_ready_message:
	ld (Disk_error_code),a
	ld b,006h					; 'DISK NOT READY' message 
le339h:
	call Print_message			; does a bit more than only a message, not all commented yet.
	ret

sub_e33dh:
	call sub_eb0ch		;e33d	cd 0c eb 	. . . 
	inc a			;e340	3c 	< 
	ld (lf520h+1),a		;e341	32 21 f5 	2 ! . 
	xor 003h		;e344	ee 03 	. . 
	ld (lf520h+2),a		;e346	32 22 f5 	2 " . 
	call sub_eb23h		;e349	cd 23 eb 	. # . 
	call Recalibrate_drive		;e34c	cd 39 e7 	. 9 . 
	ld a,(lf520h+1)		;e34f	3a 21 f5 	: ! . 
	call sub_eb23h		;e352	cd 23 eb 	. # . 
	ld a,001h		;e355	3e 01 	> . 
	ld (RW_cmd_track),a		;e357	32 39 f5 	2 9 . 
	ld hl,(lf589h)		;e35a	2a 89 f5 	* . . 
	push hl			;e35d	e5 	. 
	call sub_e496h		;e35e	cd 96 e4 	. . . 
	pop hl			;e361	e1 	. 
	ld de,0000dh		;e362	11 0d 00 	. . . 
	add hl,de			;e365	19 	. 
	ld b,080h		;e366	06 80 	. . 
	ld de,0x0020		;e368	11 20 00 	.   . 
le36bh:
	call sub_e664h		;e36b	cd 64 e6 	. d . 
	add hl,de			;e36e	19 	. 
	djnz le36bh		;e36f	10 fa 	. . 
	ld a,(lf520h+2)		;e371	3a 22 f5 	: " . 
	call sub_eb23h		;e374	cd 23 eb 	. # . 
	call sub_e74ch		;e377	cd 4c e7 	. L . 
	jr le38bh		;e37a	18 0f 	. . 
le37ch:
	call sub_e51fh		;e37c	cd 1f e5 	. . . 
	call sub_e79ah		;e37f	cd 9a e7 	. . . 
	ld a,(lf520h+2)		;e382	3a 22 f5 	: " . 
	call sub_eb23h		;e385	cd 23 eb 	. # . 
	call sub_e847h		;e388	cd 47 e8 	. G . 
le38bh:
	ld hl,RW_cmd_track		;e38b	21 39 f5 	! 9 . 
	ld a,(lf571h)		;e38e	3a 71 f5 	: q . 
	cp (hl)			;e391	be 	. 
	jr z,le3a3h		;e392	28 0f 	( . 
	inc (hl)			;e394	34 	4 
	ld a,(lf520h+1)		;e395	3a 21 f5 	: ! . 
	call sub_eb23h		;e398	cd 23 eb 	. # . 
	call Seek_to_track		;e39b	cd 2c e7 	. , . 
	ld a,(RW_cmd_track)		;e39e	3a 39 f5 	: 9 . 
	jr le37ch		;e3a1	18 d9 	. . 
le3a3h:
	ld a,(lf520h+2)		;e3a3	3a 22 f5 	: " . 
	call sub_eb23h		;e3a6	cd 23 eb 	. # . 
	call Recalibrate_drive		;e3a9	cd 39 e7 	. 9 . 
	ld a,(lf520h+1)		;e3ac	3a 21 f5 	: ! . 
	call sub_eb23h		;e3af	cd 23 eb 	. # . 
	call Recalibrate_drive		;e3b2	cd 39 e7 	. 9 . 
	call sub_e51fh		;e3b5	cd 1f e5 	. . . 
	ld a,001h		;e3b8	3e 01 	> . 
	call sub_e79ah		;e3ba	cd 9a e7 	. . . 
	ld a,(lf520h+2)		;e3bd	3a 22 f5 	: " . 
	call sub_eb23h		;e3c0	cd 23 eb 	. # . 
	call sub_e847h		;e3c3	cd 47 e8 	. G . 
	ret			;e3c6	c9 	. 
sub_e3c7h:
	call sub_e67dh		;e3c7	cd 7d e6 	. } . 
	ld hl,lf6b3h+1		;e3ca	21 b4 f6 	! . . 
	call sub_e689h		;e3cd	cd 89 e6 	. . . 
	ld hl,05052h		;e3d0	21 52 50 	! R P 
	xor a			;e3d3	af 	. 
	cp (hl)			;e3d4	be 	. 
	jr nz,le3dah		;e3d5	20 03 	  . 
	ld hl,05002h		;e3d7	21 02 50 	! . P 
le3dah:
	ld de,lf6b3h+2		;e3da	11 b5 f6 	. . . 
	call copy_8_bytes		;e3dd	cd d4 e5 	. . . 
	call sub_e2cch		;e3e0	cd cc e2 	. . . 
	jp c,exit_and_set_disk_off_timer		;e3e3	da 8e e2 	. . . 
	call sub_e65ah		;e3e6	cd 5a e6 	. Z . 
	ld a,(05050h)		;e3e9	3a 50 50 	: P P 
	call sub_eb23h		;e3ec	cd 23 eb 	. # . 
	ld a,(PDOS_flags)		;e3ef	3a 24 f5 	: $ . 
	and 080h		;e3f2	e6 80 	. . 
	ld (PDOS_flags),a		;e3f4	32 24 f5 	2 $ . 
	call sub_e2c8h		;e3f7	cd c8 e2 	. . . 
	ld de,lf6b3h+1		;e3fa	11 b4 f6 	. . . 
	ld (lf52ch),de		;e3fd	ed 53 2c f5 	. S , . 
	call sub_e31bh		;e401	cd 1b e3 	. . . 
	call sub_e2dch		;e404	cd dc e2 	. . . 
	jp c,exit_and_set_disk_off_timer		;e407	da 8e e2 	. . . 
	ld hl,(lf589h)		;e40a	2a 89 f5 	* . . 
	ld (ix_pointer),hl		;e40d	22 32 f5 	" 2 . 
le410h:
	ld hl,(ix_pointer)		;e410	2a 32 f5 	* 2 . 
	ld (lf589h),hl		;e413	22 89 f5 	" . . 
	ld de,lf693h		;e416	11 93 f6 	. . . 
	ld (lf52ch),de		;e419	ed 53 2c f5 	. S , . 
	ld a,(VIDEO_ram)		;e41d	3a 00 50 	: . P 
	call sub_eb23h		;e420	cd 23 eb 	. # . 
	ld a,001h		;e423	3e 01 	> . 
	ld (lf520h),a		;e425	32 20 f5 	2   . 
le428h:
	call sub_e5e0h		;e428	cd e0 e5 	. . . 
	jr z,le439h		;e42b	28 0c 	( . 
	ld hl,lf520h		;e42d	21 20 f5 	!   . 
	ld a,(lf56dh)		;e430	3a 6d f5 	: m . 
	cp (hl)			;e433	be 	. 
	jr z,le439h		;e434	28 03 	( . 
	inc (hl)			;e436	34 	4 
	jr le428h		;e437	18 ef 	. . 
le439h:
	ld hl,(ix_pointer)		;e439	2a 32 f5 	* 2 . 
	ld (lf589h),hl		;e43c	22 89 f5 	" . . 
	ld de,lf6b3h+1		;e43f	11 b4 f6 	. . . 
	ld (lf52ch),de		;e442	ed 53 2c f5 	. S , . 
	ld a,(05050h)		;e446	3a 50 50 	: P P 
	call sub_eb23h		;e449	cd 23 eb 	. # . 
le44ch:
	call sub_e2ebh		;e44c	cd eb e2 	. . . 
	jp c,exit_and_set_disk_off_timer		;e44f	da 8e e2 	. . . 
	call sub_e6d3h		;e452	cd d3 e6 	. . . 
	ld a,(lf520h)		;e455	3a 20 f5 	:   . 
	dec a			;e458	3d 	= 
	ld (lf520h),a		;e459	32 20 f5 	2   . 
	jr nz,le44ch		;e45c	20 ee 	  . 
	ld hl,lf6a2h		;e45e	21 a2 f6 	! . . 
	ld a,(lf6b3h)		;e461	3a b3 f6 	: . . 
	cp (hl)			;e464	be 	. 
	jr nz,le410h		;e465	20 a9 	  . 
	ld a,040h		;e467	3e 40 	> @ 
	cp (hl)			;e469	be 	. 
	jp nz,le1a1h		;e46a	c2 a1 e1 	. . . 
	ld hl,0f69fh		;e46d	21 9f f6 	! . . 
	inc (hl)			;e470	34 	4 
	inc hl			;e471	23 	# 
	ld bc,00013h		;e472	01 13 00 	. . . 
	call sub_e68ch		;e475	cd 8c e6 	. . . 
	ld hl,0f69fh		;e478	21 9f f6 	! . . 
	ld de,0f6c0h		;e47b	11 c0 f6 	. . . 
	ld bc,00014h		;e47e	01 14 00 	. . . 
	ldir		;e481	ed b0 	. . 
	ld de,lf693h		;e483	11 93 f6 	. . . 
	ld (lf52ch),de		;e486	ed 53 2c f5 	. S , . 
	call sub_eb0ch		;e48a	cd 0c eb 	. . . 
	call sub_e2cch		;e48d	cd cc e2 	. . . 
	jp c,exit_and_set_disk_off_timer		;e490	da 8e e2 	. . . 
	jp le410h		;e493	c3 10 e4 	. . . 
sub_e496h:
	ld (lf52eh),hl		;e496	22 2e f5 	" . . 
	ld a,010h		;e499	3e 10 	> . 
	ld (lf56dh),a		;e49b	32 6d f5 	2 m . 
	ld a,(RW_cmd_track)		;e49e	3a 39 f5 	: 9 . 
	call sub_e79ah		;e4a1	cd 9a e7 	. . . 
	ret			;e4a4	c9 	. 
sub_e4a5h:
	ld a,002h		;e4a5	3e 02 	> . 
	call sub_eb23h		;e4a7	cd 23 eb 	. # . 
	call Recalibrate_drive		;e4aa	cd 39 e7 	. 9 . 
	ld a,001h		;e4ad	3e 01 	> . 
	ld (RW_cmd_track),a		;e4af	32 39 f5 	2 9 . 
	call sub_eb23h		;e4b2	cd 23 eb 	. # . 
	call Recalibrate_drive		;e4b5	cd 39 e7 	. 9 . 
le4b8h:
	ld hl,(lf589h)		;e4b8	2a 89 f5 	* . . 
	call sub_e496h		;e4bb	cd 96 e4 	. . . 
	ld a,002h		;e4be	3e 02 	> . 
	call sub_eb23h		;e4c0	cd 23 eb 	. # . 
	ld hl,(lf589h)		;e4c3	2a 89 f5 	* . . 
	ld bc,Cartridge_ROM		;e4c6	01 00 10 	. . . 
	add hl,bc			;e4c9	09 	. 
	call sub_e496h		;e4ca	cd 96 e4 	. . . 
	call sub_e4e0h		;e4cd	cd e0 e4 	. . . 
	ld hl,RW_cmd_track		;e4d0	21 39 f5 	! 9 . 
	ld a,(lf571h)		;e4d3	3a 71 f5 	: q . 
	cp (hl)			;e4d6	be 	. 
	ret z			;e4d7	c8 	. 
	inc (hl)			;e4d8	34 	4 
	ld a,001h		;e4d9	3e 01 	> . 
	call sub_eb23h		;e4db	cd 23 eb 	. # . 
	jr le4b8h		;e4de	18 d8 	. . 
sub_e4e0h:
	ld bc,Cartridge_ROM		;e4e0	01 00 10 	. . . 
	ld (lf57dh),bc		;e4e3	ed 43 7d f5 	. C } . 
	ld ix,lf57dh		;e4e7	dd 21 7d f5 	. ! } . 
	ld hl,(lf589h)		;e4eb	2a 89 f5 	* . . 
	push hl			;e4ee	e5 	. 
	pop de			;e4ef	d1 	. 
	add hl,bc			;e4f0	09 	. 
le4f1h:
	call read_byte_bank_0-0x88c5		;e4f1	cd 3e 61 	. > a 
	ld b,c			;e4f4	41 	A 
	ex de,hl			;e4f5	eb 	. 
	call read_byte_bank_0-0x88c5		;e4f6	cd 3e 61 	. > a 
	ld a,c			;e4f9	79 	y 
	cp b			;e4fa	b8 	. 
	jr nz,le505h		;e4fb	20 08 	  . 
	inc hl			;e4fd	23 	# 
	inc de			;e4fe	13 	. 
	dec (ix+000h)		;e4ff	dd 35 00 	. 5 . 
	jr nz,le4f1h		;e502	20 ed 	  . 
	ret			;e504	c9 	. 
le505h:
	ld b,00ch		;e505	06 0c 	. . 
	call Print_message		;e507	cd 97 e6 	. . . 
le50ah:
	jp exit_and_set_disk_off_timer		;e50a	c3 8e e2 	. . . 
sub_e50dh:
	ld a,001h		;e50d	3e 01 	> . 
	call sub_eb23h		;e50f	cd 23 eb 	. # . 
	ld a,003h		;e512	3e 03 	> . 
	ld (RW_cmd_track),a		;e514	32 39 f5 	2 9 . 
	call Seek_to_track		;e517	cd 2c e7 	. , . 
	ld a,001h		;e51a	3e 01 	> . 
	ld (RW_cmd_sector),a		;e51c	32 3b f5 	2 ; . 
sub_e51fh:
	ld hl,(lf589h)		;e51f	2a 89 f5 	* . . 
le522h:
	ld (lf52eh),hl		;e522	22 2e f5 	" . . 
	ret			;e525	c9 	. 
sub_e526h:
	ld hl,dir_buffer		;e526	21 d5 f6 	! . . 
	jr le522h		;e529	18 f7 	. . 
sub_e52bh:
	call sub_e67dh		;e52b	cd 7d e6 	. } . 
sub_e52eh:
	call sub_e2cch		;e52e	cd cc e2 	. . . 
	jp c,le50ah		;e531	da 0a e5 	. . . 
	ld hl,(lf52ch)		;e534	2a 2c f5 	* , . 
	ld bc,0000dh		;e537	01 0d 00 	. . . 
	add hl,bc			;e53a	09 	. 
	ret			;e53b	c9 	. 
sub_e53ch:
	ld a,001h		;e53c	3e 01 	> . 

sub_e53eh:
	ld (lf56dh),a				; e53e	32 6d f5 	2 m . 
	ld hl,PDOS_flags			; e541	21 24 f5 	! $ . 
	set 1,(hl)					; indicate bank 0 = active
	ret							; e546	c9 	. 

sub_e547h:
	ld a,(Retry_counter+1)		;e547	3a 1d f5 	: . . 
	cp 02bh		;e54a	fe 2b 	. + 
	ret nz			;e54c	c0 	. 
	ld hl,PDOS_flags		;e54d	21 24 f5 	! $ . 
	bit 4,(hl)		;e550	cb 66 	. f 
	res 4,(hl)		;e552	cb a6 	. . 
	ret nz			;e554	c0 	. 
	set 4,(hl)		;e555	cb e6 	. . 
	ret			;e557	c9 	. 

plot_activity_letter:
	ld hl,(mon_status_io)		; contains address where to plot the letter
	inc hl						; two places to the right (skip possible color modifiers?)
	inc hl
	ld (hl),a					; plot the letter
	ld bc,00800h				; add 800 so hl points to attribute memopry (for model M)
	add hl,bc
	ret

sub_e563h:
	ld a,010h						;e563	3e 10 	> . 
	call sub_e53eh					;e565	cd 3e e5 	. > . 
	res 1,(hl)						;bank 0 NOT active
	ret			;e56a	c9 	. 
sub_e56bh:
	ld hl,PDOS_flags		;e56b	21 24 f5 	! $ . 
	bit 7,(hl)		;e56e	cb 7e 	. ~ 
	ret			;e570	c9 	. 

; delay 
delay:
	ld bc,0x0000
_delay_loop:
	djnz _delay_loop
	dec c
	jr nz,_delay_loop
	ret

sub_e57ah:
	push hl			;e57a	e5 	. 
	push bc			;e57b	c5 	. 
	ld bc,02710h		;e57c	01 10 27 	. . ' 
	push af			;e57f	f5 	. 
	push de			;e580	d5 	. 
	ld a,030h		;e581	3e 30 	> 0 
	ld (de),a			;e583	12 	. 
le584h:
	or a			;e584	b7 	. 
	push hl			;e585	e5 	. 
	sbc hl,bc		;e586	ed 42 	. B 
	jr nc,le5a8h		;e588	30 1e 	0 . 
	ld hl,0000bh		;e58a	21 0b 00 	! . . 
	sbc hl,bc		;e58d	ed 42 	. B 
	jr z,le5c5h		;e58f	28 34 	( 4 
	ld hl,00065h		;e591	21 65 00 	! e . 
	sbc hl,bc		;e594	ed 42 	. B 
	jr z,le5bch		;e596	28 24 	( $ 
	ld hl,003e9h		;e598	21 e9 03 	! . . 
	sbc hl,bc		;e59b	ed 42 	. B 
	jr z,le5b3h		;e59d	28 14 	( . 
	call sub_e5aeh		;e59f	cd ae e5 	. . . 
	pop hl			;e5a2	e1 	. 
	ld bc,003e8h		;e5a3	01 e8 03 	. . . 
	jr le584h		;e5a6	18 dc 	. . 
le5a8h:
	ld a,(de)			;e5a8	1a 	. 
	inc a			;e5a9	3c 	< 
	ld (de),a			;e5aa	12 	. 
	pop af			;e5ab	f1 	. 
	jr le584h		;e5ac	18 d6 	. . 
sub_e5aeh:
	inc de			;e5ae	13 	. 
	ld a,030h		;e5af	3e 30 	> 0 
	ld (de),a			;e5b1	12 	. 
	ret			;e5b2	c9 	. 
le5b3h:
	call sub_e5aeh		;e5b3	cd ae e5 	. . . 
	pop hl			;e5b6	e1 	. 
	ld bc,00064h		;e5b7	01 64 00 	. d . 
	jr le584h		;e5ba	18 c8 	. . 
le5bch:
	call sub_e5aeh		;e5bc	cd ae e5 	. . . 
	pop hl			;e5bf	e1 	. 
	ld bc,0000ah		;e5c0	01 0a 00 	. . . 
	jr le584h		;e5c3	18 bf 	. . 
le5c5h:
	call sub_e5aeh		;e5c5	cd ae e5 	. . . 
	pop hl			;e5c8	e1 	. 
	ld bc,00030h		;e5c9	01 30 00 	. 0 . 
	add hl,bc			;e5cc	09 	. 
	ld a,l			;e5cd	7d 	} 
	ld (de),a			;e5ce	12 	. 
	pop de			;e5cf	d1 	. 
	pop af			;e5d0	f1 	. 
	pop bc			;e5d1	c1 	. 
	pop hl			;e5d2	e1 	. 
	ret			;e5d3	c9 	. 

copy_8_bytes:
	ld bc,8				;e5d4	01 08 00 	. . . 
	ldir		;e5d7	ed b0 	. . 
	inc hl			;e5d9	23 	# 

copy_3_bytes:
	ld bc,3		;e5da	01 03 00 	. . . 
	ldir		;e5dd	ed b0 	. . 
	ret			;e5df	c9 	. 

sub_e5e0h:
	call sub_e2e7h		;e5e0	cd e7 e2 	. . . 
	ld b,a			;e5e3	47 	G 
	ld hl,(lf589h)		;e5e4	2a 89 f5 	* . . 
	ld de,(lf56bh)		;e5e7	ed 5b 6b f5 	. [ k . 
	add hl,de			;e5eb	19 	. 
	ld (lf589h),hl		;e5ec	22 89 f5 	" . . 
	ld a,(lf6a2h)		;e5ef	3a a2 f6 	: . . 
	ld hl,lf6b3h		;e5f2	21 b3 f6 	! . . 
	cp (hl)			;e5f5	be 	. 
	ret			;e5f6	c9 	. 
sub_e5f7h:
	call sub_e56bh		;e5f7	cd 6b e5 	. k . 
	jr z,le608h		;e5fa	28 0c 	( . 
	call sub_e2c8h		;e5fc	cd c8 e2 	. . . 
	ld hl,05052h		;e5ff	21 52 50 	! R P 
	ld de,lf6a4h		;e602	11 a4 f6 	. . . 
	call copy_8_bytes		;e605	cd d4 e5 	. . . 
le608h:
	ld de,(lf52ch)		;e608	ed 5b 2c f5 	. [ , . 
	ld c,017h		;e60c	0e 17 	. . 
	call sub_e706h		;e60e	cd 06 e7 	. . . 
	ret			;e611	c9 	. 
sub_e612h:
	call sub_e526h		;e612	cd 26 e5 	. & . 
	call sub_e774h		;e615	cd 74 e7 	. t . 
	call sub_ea7ch		;e618	cd 7c ea 	. | . 
	call sub_e63ch		;e61b	cd 3c e6 	. < . 
	push ix		;e61e	dd e5 	. . 
	call sub_e774h		;e620	cd 74 e7 	. t . 
	call sub_ea7ch		;e623	cd 7c ea 	. | . 
	call sub_e63ch		;e626	cd 3c e6 	. < . 
	pop hl			;e629	e1 	. 
	push ix		;e62a	dd e5 	. . 
	pop bc			;e62c	c1 	. 
	add hl,bc			;e62d	09 	. 
	ld a,(lf572h)		;e62e	3a 72 f5 	: r . 
	ld e,a			;e631	5f 	_ 
	ex de,hl			;e632	eb 	. 
	sbc hl,de		;e633	ed 52 	. R 
	ld (lf57bh),hl		;e635	22 7b f5 	" { . 
	call sub_e547h		;e638	cd 47 e5 	. G . 
	ret			;e63b	c9 	. 
sub_e63ch:
	ld hl,lf6e5h		;e63c	21 e5 f6 	! . . 
	ld b,040h		;e63f	06 40 	. @ 
	ld ix,0x0000		;e641	dd 21 00 00 	. ! . . 
le645h:
	ld de,0x0000		;e645	11 00 00 	. . . 
le648h:
	ld a,(hl)			;e648	7e 	~ 
	cp 000h		;e649	fe 00 	. . 
	jr z,le64fh		;e64b	28 02 	( . 
	inc ix		;e64d	dd 23 	. # 
le64fh:
	inc de			;e64f	13 	. 
	inc hl			;e650	23 	# 
	ld a,010h		;e651	3e 10 	> . 
	cp e			;e653	bb 	. 
	jr nz,le648h		;e654	20 f2 	  . 
	add hl,de			;e656	19 	. 
	djnz le645h		;e657	10 ec 	. . 
	ret			;e659	c9 	. 
sub_e65ah:
	ld hl,(lf52ch)		;e65a	2a 2c f5 	* , . 
sub_e65dh:
	ld bc,0000dh		;e65d	01 0d 00 	. . . 
	add hl,bc			;e660	09 	. 
	ld c,(hl)			;e661	4e 	N 
	jr le667h		;e662	18 03 	. . 
sub_e664h:
	call read_byte_bank_0-0x88c5		;e664	cd 3e 61 	. > a 
le667h:
	ld a,c			;e667	79 	y 
	bit 1,a		;e668	cb 4f 	. O 
	ret z			;e66a	c8 	. 
	ld a,(Retry_counter+1)		;e66b	3a 1d f5 	: . . 
	cp 010h		;e66e	fe 10 	. . 
	jr z,le67ah		;e670	28 08 	( . 
	cp 014h		;e672	fe 14 	. . 
	ret z			;e674	c8 	. 
	ld a,00bh		;e675	3e 0b 	> . 
	ld (Disk_error_code),a		;e677	32 1e f5 	2 . . 
le67ah:
	jp exit_and_set_disk_off_timer		;e67a	c3 8e e2 	. . . 
sub_e67dh:
	ld hl,(lf52ch)		;e67d	2a 2c f5 	* , . 
	ld bc,0000ch		;e680	01 0c 00 	. . . 
	add hl,bc			;e683	09 	. 
	ld bc,00014h		;e684	01 14 00 	. . . 
	jr sub_e68ch		;e687	18 03 	. . 
sub_e689h:
	ld bc,00021h		;e689	01 21 00 	. ! . 
sub_e68ch:
	xor a			;e68c	af 	. 
	push hl			;e68d	e5 	. 
	pop de			;e68e	d1 	. 
	inc de			;e68f	13 	. 
	ld (lf693h),a		;e690	32 93 f6 	2 . . 
	ld (hl),a			;e693	77 	w 
	ldir		;e694	ed b0 	. . 
	ret			;e696	c9 	. 
;
; output a message
; message number in B
;	
Print_message:
	push bc							; save message number

	call sub_ecd5h					;e698	cd d5 ec 	. . . 

	ld a,(last_used_drive)			; get last addressed drive number
	ld (Active_drive),a				; save it
	inc a							; add 1

	call sub_f27bh						; no idea yet... 

	pop bc							; message number back
	ld hl,(mon_status_io)			; where to plot ststus character
	ld a,b							; save msg number in A
	ld bc,8							; add 8 to position
	add hl,bc
	ld b,a							; message number back in B
	ld a,(Disk_flag)				; get error code
	add a,030h						; turn into a digit
	ld (hl),a						; put on screen
	inc hl							; next position
	ld a,':'						; print colon
	ld (hl),a
	inc hl							; space after colon
	xor a
	ld (hl),a
	inc hl							; string prints after error digit
;
; print string from table
; b contains string index NUMBER 1-12
; table = byte len of string, string bytes, len, bytes, etc.
;
print_string:
	push hl						; save hl
	ld hl,stringtable_start		; 
	jr le6c8h					; jump to decrement 
skip_string:
	ld d,000h					; de = len of this string 
	ld e,(hl)					;  
	add hl,de					; skip len bytes 
	inc hl						; point to len of next string
le6c8h:
	djnz skip_string		    ; skip until we're at the right index.
	pop de						; get destination address for string
	ld c,(hl)					; length in C
	ld b,000h					; clear hi byte of count
	inc hl						; skip len, hl points to string
	ldir						; copy to screen
	scf							; set carry
	ret

sub_e6d3h:
	ld hl,(lf589h)		;e6d3	2a 89 f5 	* . . 
	ld de,(lf56bh)		;e6d6	ed 5b 6b f5 	. [ k . 
	add hl,de			;e6da	19 	. 
	ld (lf589h),hl		;e6db	22 89 f5 	" . . 
	xor a			;e6de	af 	. 
	ld hl,(lf530h)		;e6df	2a 30 f5 	* 0 . 
	sbc hl,de		;e6e2	ed 52 	. R 
	ld (lf530h),hl		;e6e4	22 30 f5 	" 0 . 
	ret			;e6e7	c9 	. 
sub_e6e8h:
	xor a			;e6e8	af 	. 
	ld hl,050f0h		;e6e9	21 f0 50 	! . P 
	ld (hl),a			;e6ec	77 	w 
	push hl			;e6ed	e5 	. 
	pop de			;e6ee	d1 	. 
	inc de			;e6ef	13 	. 
	ld bc,00710h		;e6f0	01 10 07 	. . . 
	ldir		;e6f3	ed b0 	. . 
	ret			;e6f5	c9 	. 

plot_activity_D:
	ld a,'D'
	call plot_activity_letter
	ld a,005h						; attribute (flash?)
	ld (hl),a						; set it
	ret

clear_activity_letter:
	xor a							; zero 
	call plot_activity_letter		;  
	ld (hl),a						; atrribut also zero
	ret

sub_e705h:
	ld c,a			;e705	4f 	O 
sub_e706h:
	ld de,(lf52ch)		;e706	ed 5b 2c f5 	. [ , . 
	ex de,hl			;e70a	eb 	. 
	ld (0f579h),hl		;e70b	22 79 f5 	" y . 
	ld hl,0f578h		;e70e	21 78 f5 	! x . 
	ld (hl),c			;e711	71 	q 
	call sub_f2fdh		;e712	cd fd f2 	. . . 
	ld hl,(lf57bh)		;e715	2a 7b f5 	* { . 
	ld a,l			;e718	7d 	} 
	ld b,h			;e719	44 	D 
	ret			;e71a	c9 	. 



Sense_int_and_Specify:
	di										; 
	call RETI								; signal other listeners in the chain
	call Sense_disk_interrupt_status
	call Set_time_out_comm_irq				
	ld hl,DISK_SPECIFY_COMMAND
	call send_disk_command
	ret

Seek_to_track:
	ld a,(RW_cmd_track)						; get 1- based track #
	dec a									; make 0-based
	ld (SEEK_cmd_track),a					; store in seek command
	ld hl,DISK_SEEK_Command
	push hl									; command on stack
	jr send_seek_or_recalibrate				; send command with dummy irq handler

Recalibrate_drive:
	ld hl,DISK_RECALIBRATE_Command
	push hl

send_seek_or_recalibrate:
	ld hl,EI_RETI							; NOP interrupt code (just EI/RETI)
	ld (06135h),hl							; set as interrupt vector
	pop hl									; get e743	e1 	. 
	call send_disk_command
	halt
	call Sense_disk_interrupt_status
	ret

sub_e74ch:
	call Recalibrate_drive		;e74c	cd 39 e7 	. 9 . 
	ld hl,dir_buffer		;e74f	21 d5 f6 	! . . 
	ld bc,00100h		;e752	01 00 01 	. . . 
	call sub_e68ch		;e755	cd 8c e6 	. . . 
	ld a,001h		;e758	3e 01 	> . 
	ld (RW_cmd_sector),a		;e75a	32 3b f5 	2 ; . 
	ld (RW_cmd_track),a		;e75d	32 39 f5 	2 9 . 
le760h:
	push af			;e760	f5 	. 
	call sub_e53ch		;e761	cd 3c e5 	. < . 
	call sub_e526h		;e764	cd 26 e5 	. & . 
	call sub_e84fh		;e767	cd 4f e8 	. O . 
	pop af			;e76a	f1 	. 
	cp 010h		;e76b	fe 10 	. . 
	ret z			;e76d	c8 	. 
	inc a			;e76e	3c 	< 
	ld (RW_cmd_sector),a		;e76f	32 3b f5 	2 ; . 
	jr le760h		;e772	18 ec 	. . 
sub_e774h:
	ld a,008h		;e774	3e 08 	> . 
	call sub_e53eh				; returns PDOS-flags address in HL
	bit 3,(hl)					; check bit 3: 
	jr z,tst_bit2				; not set
	res 3,(hl)					; idf set, reset it

le77fh:
	set 2,(hl)					; and set bit 2.
	ld a,001h					; start with sector 1.
	jr set_sctr				

tst_bit2:
	bit 2,(hl)					; e785	cb 56 	. V 
	jr z,le77fh					; not set, set it and start at sector 1

	res 2,(hl)					; reset 2
	set 3,(hl)					; set 3
	ld a,009h					; and start at sector 9

set_sctr:
	ld (RW_cmd_sector),a		; set sector
	ld hl,lf575h				; get pointer to track
	ld a,001h					; add 1 
	add a,(hl)
	jr set_trk	

sub_e79ah:
	push af			;e79a	f5 	. 
	ld a,001h		;e79b	3e 01 	> . 
	ld (RW_cmd_sector),a		;e79d	32 3b f5 	2 ; . 
	pop af			;e7a0	f1 	. 

set_trk:
	ld iy,DISK_RW_Command
	ld (iy+003h),a					; set track
	call Seek_to_track

; sub_e7abh

sub_e7abh:
	ld a,00fh								; 15  tries
	ld (Retry_counter),a

try_read_loop:
	ld hl,Disk_Read_ended_irq
	call issue_Disk_read_command		; activate bank 0, send command, hl is irq vector
	jr nz,le7beh						; NZ: BANK 0 IS ACTIVE
le7b8h:
	call read_disk_bytes-0x88c5		;e7b8	cd 45 61 	. E a 
	jp busy_wait_for_interrupt		;e7bb	c3 5f e9 	. _ . 
le7beh:
	call dsk_in_loop-0x88c5				; = 06148h		send command, hl is irq vector
	jp busy_wait_for_interrupt

Post_read_irq_code:
	call Get_7_disk_status_bytes				; returns loop value in b, and Z set if the right drive was ready
	jr z,le7cbh									; write finished!
	djnz try_read_loop							; decrement b and retry 


le7cbh:
	ld a,010h									;e7cb	3e 10 	> . 
	ld (lf56dh),a								;e7cd	32 6d f5 	2 m . 
	call sub_e7dch								;e7d0	cd dc e7 	. . . 
	ld hl,PDOS_flags		;e7d3	21 24 f5 	! $ . 
	res 1,(hl)		;e7d6	cb 8e 	. . 
	call sub_e96fh		;e7d8	cd 6f e9 	. o . 
	ret			;e7db	c9 	. 

sub_e7dch:
	ld hl,(lf52eh)		;e7dc	2a 2e f5 	* . . 
	call Test_FDOS_bit1		;e7df	cd ee e8 	. . . 
	jr z,le7e9h		;e7e2	28 05 	( . 
	ld b,(hl)			;e7e4	46 	F 
	inc hl			;e7e5	23 	# 
	ld c,(hl)			;e7e6	4e 	N 
	jr le7f2h		;e7e7	18 09 	. . 
le7e9h:
	call read_byte_bank_0-0x88c5		;e7e9	cd 3e 61 	. > a 
	ld a,c			;e7ec	79 	y 
	ld b,a			;e7ed	47 	G 
	inc hl			;e7ee	23 	# 
	call read_byte_bank_0-0x88c5		;e7ef	cd 3e 61 	. > a 
le7f2h:
	ld a,c			;e7f2	79 	y 
	cp 0edh		;e7f3	fe ed 	. . 
	jr z,le809h		;e7f5	28 12 	( . 
	ld a,b			;e7f7	78 	x 
	cp 0f3h		;e7f8	fe f3 	. . 
	ret nz			;e7fa	c0 	. 
	ld a,(Retry_counter+1)		;e7fb	3a 1d f5 	: . . 
	cp 02eh		;e7fe	fe 2e 	. . 
	jp z,le809h		;e800	ca 09 e8 	. . . 
	cp 013h		;e803	fe 13 	. . 
	jp z,le980h		;e805	ca 80 e9 	. . . 
	ret			;e808	c9 	. 
le809h:
	ld a,(RW_cmd_track)		;e809	3a 39 f5 	: 9 . 
	cp 001h		;e80c	fe 01 	. . 
	ret nz			;e80e	c0 	. 
	ld a,(Retry_counter+1)		;e80f	3a 1d f5 	: . . 
	cp 039h		;e812	fe 39 	. 9 
	ret z			;e814	c8 	. 
	ld b,009h		;e815	06 09 	. . 
	call Print_message		;e817	cd 97 e6 	. . . 
	ld a,(Retry_counter+1)		;e81a	3a 1d f5 	: . . 
	cp 037h		;e81d	fe 37 	. 7 
	ret z			;e81f	c8 	. 
	cp 038h		;e820	fe 38 	. 8 
	ret z			;e822	c8 	. 
	cp 00eh		;e823	fe 0e 	. . 
	ret z			;e825	c8 	. 
	cp 00dh		;e826	fe 0d 	. . 
	ret z			;e828	c8 	. 
	ld a,00ch		;e829	3e 0c 	> . 
	ld (Disk_error_code),a		;e82b	32 1e f5 	2 . . 
	jp exit_and_set_disk_off_timer		;e82e	c3 8e e2 	. . . 
sub_e831h:
	ld a,001h		;e831	3e 01 	> . 
	ld hl,lf575h		;e833	21 75 f5 	! u . 
	add a,(hl)			;e836	86 	. 
	ld (RW_cmd_track),a		;e837	32 39 f5 	2 9 . 
	ld a,008h		;e83a	3e 08 	> . 
	call sub_e53eh		;e83c	cd 3e e5 	. > . 
	bit 3,(hl)		;e83f	cb 5e 	. ^ 
	jr z,sub_e847h		;e841	28 04 	( . 
	ld a,009h		;e843	3e 09 	> . 
	jr le849h		;e845	18 02 	. . 
sub_e847h:
	ld a,001h		;e847	3e 01 	> . 
le849h:
	ld (RW_cmd_sector),a		;e849	32 3b f5 	2 ; . 
	call Seek_to_track		;e84c	cd 2c e7 	. , . 

sub_e84fh:
	ld a,00fh									; e84f	3e 0f 	> . 
	ld (Retry_counter),a						; e851	32 1c f5 	2 . . 

try_write_loop:
	ld a,0x45									; 01000101b = Disk WRITE command
	ld hl,Disk_Write_ended_irq					; end write handler
	call issue_Disk_write_command				; e859	cd c5 e8 	. . . 

	jr nz,le864h								; FDOS bit 1 set (retry??)
	call send_disk_bytes-0x88c5					; send bytes loop.
	jp busy_wait_for_interrupt					;e861	c3 5f e9 	. _ . 

le864h:
	call dsk_out_loop-0x88c5					; 0615dh		;e864	cd 5d 61 	. ] a 
	jp busy_wait_for_interrupt					;e867	c3 5f e9 	. _ . 

Post_write_irq_code:
	call Get_7_disk_status_bytes				; returns loop value in b, and Z set if the right drive was ready
	jr z,le871h									; write finished!
	djnz try_write_loop							; decrement b and retry 

le871h:
	call sub_e563h		;e871	cd 63 e5 	. c . 
	call sub_e96fh		;e874	cd 6f e9 	. o . 
	ld hl,0693bh		;e877	21 3b 69 	! ; i 
	xor a			;e87a	af 	. 
	cp (hl)			;e87b	be 	. 
	ret z			;e87c	c8 	. 
	ld hl,(lf52eh)		;e87d	2a 2e f5 	* . . 
	push hl			;e880	e5 	. 
	ld hl,lfed5h		;e881	21 d5 fe 	! . . 
	push hl			;e884	e5 	. 
	ld (lf52eh),hl		;e885	22 2e f5 	" . . 
	call sub_e53ch		;e888	cd 3c e5 	. < . 
	call sub_e7abh		;e88b	cd ab e7 	. . . 
	pop de			;e88e	d1 	. 
	pop hl			;e88f	e1 	. 
	push hl			;e890	e5 	. 
	ld b,000h		;e891	06 00 	. . 
le893h:
	ld c,(hl)						; get value  
	bit 1,(ix+000h)					; if bank 0 acive, continue
	jr nz,le89dh
	call read_byte_bank_0-0x88c5						; read from bank 0
le89dh:
	ld a,(de)			;e89d	1a 	. 
	cp c			;e89e	b9 	. 
	jr nz,le8adh		;e89f	20 0c 	  . 
	inc hl			;e8a1	23 	# 
	inc de			;e8a2	13 	. 
	djnz le893h		;e8a3	10 ee 	. . 
	pop hl			;e8a5	e1 	. 
	ld (lf52eh),hl		;e8a6	22 2e f5 	" . . 
	call sub_e563h		;e8a9	cd 63 e5 	. c . 
	ret			;e8ac	c9 	. 
le8adh:
	call sub_e563h		;e8ad	cd 63 e5 	. c . 
	jp channel_time_out		;e8b0	c3 78 e9 	. x . 
sub_e8b3h:
	ld a,00fh		;e8b3	3e 0f 	> . 
	ld (Retry_counter),a		;e8b5	32 1c f5 	2 . . 
le8b8h:
	call sub_e8c0h		;e8b8	cd c0 e8 	. . . 
	ld e,001h		;e8bb	1e 01 	. . 
	jp le7b8h		;e8bd	c3 b8 e7 	. . . 
sub_e8c0h:
	ld hl,le916h		;e8c0	21 16 e9 	! . . 

;
; HL contains addres of interrupt code to execute 
; when read/write action finishes
; returns NZ when bit 1 of PDOS_flags is set during the transaction.
; Returns ix = PDOS_flags
;

issue_Disk_read_command:
	ld a,046h					; MFM, READ
issue_Disk_write_command:
	ld (RW_cmd_action),a		; update command
	ld (06135h),hl				; set interrupt vector address 
	ld a,0ffh					; Processor has priority over video chip for memory access
	out (DISA),a

	ld hl,DISK_RW_Command
	call send_disk_command

	ld a,11000101b				; 0c5h = command, Time constant follows, Counter mode, and generate interrupt
	out (CTC_CH1),a				; on channel 1
	ld a,001h					; time constant = 1: trigger immediately
	out (CTC_CH1),a

	ld a,(lf56bh)				;e8dd	3a 6b f5 	: k . 
	ld b,a			;e8e0	47 	G 
	ld hl,(lf52eh)		;e8e1	2a 2e f5 	* . . 
	ld c,08dh		;e8e4	0e 8d 	. . 
	ld a,(lf56dh)		;e8e6	3a 6d f5 	: m . 
	ld e,a			;e8e9	5f 	_ 

	ld a,00001101b				; Drive Enable, reset, motor on
	out (DSKCTRL),a

;
; returns NZ when FDOS bit 1 is set 
;
Test_FDOS_bit1:
	ld ix,PDOS_flags			; get internal flags
	bit 1,(ix+000h)				; bit 1 = bank 0 active
	ret




le8f7h:
	call Get_7_disk_status_bytes		;e8f7	cd 26 e9 	. & . 
	jr z,le8feh		;e8fa	28 02 	( . 
	djnz le8b8h		;e8fc	10 ba 	. . 
le8feh:
	call sub_e96fh		;e8fe	cd 6f e9 	. o . 
	ld a,(RW_cmd_sector)		;e901	3a 3b f5 	: ; . 
	cp 001h		;e904	fe 01 	. .
	call z,sub_e7dch		;e906	cc dc e7 	. . . 
	ret			;e909	c9 	. 

Disk_Write_ended_irq:
	pop hl					; remove original return address
	ld hl,Post_write_irq_code			; jump here instead.
	jr End_RW_action

Disk_Read_ended_irq:
	pop hl					; get original return address from stack
	ld hl,Post_read_irq_code			; this is the new return address
	jr End_RW_action

le916h:
	pop hl					; get original return address from stack 
	ld hl,le8f7h			; new return address.

End_RW_action:
	ld a,00001110b			; = terminal count, motor on reset
	out (DSKCTRL),a
	ld a,003h				; = command RESET
	out (CTC_CH1),a
	push hl					; make sure RETI goes here
EI_RETI:
	ei
RETI:
	reti

;
; read 7 status bytes from drive
;
Get_7_disk_status_bytes:
	ld b,007h								; expect 7 status bytes
	call Read_Disk_status_bytes
	ld a,00001100b							; disk reset, motor on
	out (DSKCTRL),a
	xor a									; Video chip gets priority for memory access again
	out (DISA),a
	
	ld hl,Retry_counter						; counter address
	dec (hl)								; one less
	ld b,(hl)								; counter value (starts at 0x0F)

sub_e937h:
	ld a,(Active_drive)						; get active drive
	inc a									; turn into 1-based 00-01>, 01-10>, 10->11, 11->100
	call mask_drive_bits					; 4 -> 0
	ld hl,Disk_status_buffer				; Disk status Byte 0 location
	cp (hl)									; was it the correct drive?
	ret										; Z = set if drive # equals ststus drive #

sub_e943h:
	di										; interrupts off
	ld a,003h								; reset disk io interrupt
	out (CTC_CH0),a
	xor a									; disk all off
	out (DSKCTRL),a

	ld a,(RW_cmd_hd_drive_select)			; get active drive select and head
	or 11001000b							; add flags: bit7+Bit6 = 1: Bit3: Not ready 
	ld (Disk_status_buffer),a				; store in disk status buffer
	ld a,002h								; Error code 2
	call Set_error_and_Not_ready_message	; Disk not ready

set_bit6_enable_ints:
	ld hl,PDOS_flags
	set 6,(hl)								; bit 6 = disk not ready flag
	ei
	ret

busy_wait_for_interrupt:
	ld bc,0x0000							; start at 0
le962h:
	inc bc									; 1 more done
	ld hl,(PDOS_flags)						; do something
	ld hl,(PDOS_flags)						; ands again (just waste some cycles)
	ld a,b									; counter back to zero (65536 iterations done)?
	or c
	jr nz,le962h							; no, wait some more...
	jr channel_time_out						; took too long!

sub_e96fh:
	call sub_e937h		;e96f	cd 37 e9 	. 7 . 
	ret z			;e972	c8 	. 
	inc hl			;e973	23 	# 
	bit 1,(hl)		;e974	cb 4e 	. N 
	jr nz,le97dh		;e976	20 05 	  . 

channel_time_out:
	call sub_e943h		;e978	cd 43 e9 	. C . 
	jr le98eh		;e97b	18 11 	. . 
le97dh:
	call sub_e943h		;e97d	cd 43 e9 	. C . 
le980h:
	ld a,009h		;e980	3e 09 	> . 
	ld hl,lf57bh		;e982	21 7b f5 	! { . 
	ld (hl),a			;e985	77 	w 
le986h:
	ld (Disk_error_code),a		;e986	32 1e f5 	2 . . 
	ld b,007h		;e989	06 07 	. . 
	call Print_message		;e98b	cd 97 e6 	. . . 
le98eh:
	jp exit_and_set_disk_off_timer		;e98e	c3 8e e2 	. . . 


Read_Disk_status_bytes:
	ld a,003h					; reset disk time-out interrupt
	out (CTC_CH1),a
	ld hl,Disk_status_buffer	; destination for Disk status
	ld a,00ch					; disk reset, motor on
	out (DSKCTRL),a
le99ch:
	call wait_channel_ready		; 
	in a,(DSKSTAT)		;e99f	db 8d 	. . 
	ld (hl),a			;e9a1	77 	w 
	inc hl			;e9a2	23 	# 
	djnz le99ch		;e9a3	10 f7 	. . 
	ret			;e9a5	c9 	. 

; HL points to command
; format: len byte (n) followed by n command bytes
send_disk_command:
	ld b,(hl)					; get length in B
le9a7h:
	inc hl						; point too next command byte
	call wait_channel_ready
	ld a,(hl)					; send next byte
	out (DSKSTAT),a
	djnz le9a7h					; until all are sent 
	ret

; waits for channel to become ready 65536 times.
; returns when channel becomes ready in that period.
; if not, an error has occurred and is dealt with.
wait_channel_ready:
	push bc
	sub a					; zero
	ld b,a					; bc all zero
	ld c,a			    	; e9b4	4f 	O 
ch_rdy_loop:
	in a,(DSKIO1)			; read status
	bit 7,a					; bit 7 set?
	jr nz,channel_ready		; yes, channel is ready
	inc bc					; inc wait loop counter
	ld a,b					; back to zero?
	or c
	jr nz,ch_rdy_loop		; no, keep waiting!
	jp channel_time_out		;e9c0	c3 78 e9 	. x . 

channel_ready:
	pop bc
	ret

send_disk_stat:
	out (DSKSTAT),a				; e9c5	d3 8d 	. . 
	call wait_channel_ready		; e9c7	cd b1 e9 	. . . 
	ret							; e9ca	c9 	. 

Set_time_out_comm_irq:
	ld hl,CTC_timer_disk		; set address of disk finished interrupt vector
	ld a,h
	ld i,a						; i = hi byte 
	ld a,l						; lo byte to CTC
	out (CTC_CH0),a
	ld a,0d5h					; 11010101 = ctrl, time constant follows, trigger rising edge
								; counter mode, generate interrupt
	out (CTC_CH0),a	

	ld a,001h					; time constant = 1: trigger immediately
	out (CTC_CH0),a

	ld hl,finished_irq_dest				; set disk ready vector
	ld (CTC_timer_disk),hl
	ld hl,finished_irq_dest+7
	ld (CTC_communication),hl			; set time out vector as well

	ld hl,disk_finished_irq		;e9e8	21 f5 e9 	! . . 
	ld de,finished_irq_dest		;e9eb	11 30 61 	. 0 a 
	ld bc,00045h		;e9ee	01 45 00 	. E . 
	ldir		;e9f1	ed b0 	. . 
	ei			;e9f3	fb 	. 
	ret			;e9f4	c9 	. 

disk_finished_irq:								; = 6130
	ld a,001h		;e9f5	3e 01 	> . 
	out (BANK_SWITCH),a		;e9f7	d3 94 	. . 
	jp EI_RETI		;e9f9	c3 23 e9 	. # . 

	ld a,001h		;							; = 6137
	out (BANK_SWITCH),a		;e9fe	d3 94 	. . 
	jp lea6ah		;ea00	c3 6a ea 	. j . 
read_byte_bank_0:
	xor a			;ea03	af 	. 				; = 613e
	out (BANK_SWITCH),a		;ea04	d3 94 	. . 
	ld c,(hl)			  ; read from bank 0
	jp bank_1_on-0x88c5		; reactivate bank 1

read_disk_bytes:					; = 06145h    = read_disk_bytes-0x88c5
	xor a							; switch to normal memory
	out (BANK_SWITCH),a
dsk_in_loop:	
	in a,(DSKCTRL)					; read disk status
	rra								; byte ready?
	jp nc,dsk_in_loop-0x88c5		; no, keep waiting

	ini								; read byte and store
	jp nz,dsk_in_loop-0x88c5		; more to read
	dec e							; more sectors coming?
	jp nz,dsk_in_loop-0x88c5		; yes!
	jp dsk_io_done-0x88c5			; no, clean up

send_disk_bytes:					; = 0615ah = send_disk_bytes-0x88c5
	xor a							; normal memory
	out (BANK_SWITCH),a
dsk_out_loop:
	in a,(DSKCTRL)					; read disk status
	rra								; byte ready?
	jp nc,dsk_out_loop-0x88c5		; no, keep waiting
	outi							; send a byte
	jp nz,dsk_out_loop-0x88c5		; more to write
	dec e							; decrement sector counter
	jp nz,dsk_out_loop-0x88c5		; more to do!

dsk_io_done:						; ea31
	ld a,00eh						; 00001110 = terminal count, reset, motor on
	out (DSKCTRL),a
bank_1_on:
	ld a,001h						; activate PDOS memory
	out (BANK_SWITCH),a
	ret

;
; disk time out interrupt.
; will ignore 1024 interrupts (one is coming every ~0.02 second)
; so after about 20 seconds it will fire
;
irq_code:						; actually runs at 6137h 

move_offset:	equ		irq_code-timeout_irq_dest

	di						
	push af
	ld a,(irq_loopcounter1-move_offset)			; decrement inner loop.
	dec a
	ld (irq_loopcounter1-move_offset),a
	jr nz,exit_irq								; exit when not zero
	ld a,(irq_loopcounter2-move_offset)			; decrement outer loop
	dec a
	ld (irq_loopcounter2-move_offset),a
	jr nz,exit_irq								; exit when not zero

	ld a,003h									; control word, reset.
	out (CTC_CH1),a	
	push hl
	ld a,004h									; reset
	out (DSKCTRL),a
	ld a,001h									; activate bank 1, where PDOS resides.
	out (BANK_SWITCH),a
	ld hl,PDOS_flags							; set flag 6 (disk time out)
	set 6,(hl)
	xor a										; back to normal memory
	out (BANK_SWITCH),a
	pop hl										; restore hl
exit_irq:
	pop af										; and flags
	ei				
	reti										; allow daisy chain interrupts

irq_loopcounter1:	
	db				0xff 
irq_loopcounter2:	
	db				0x04



lea6ah:
	di											;ea6a	f3 	. 
	call sub_e943h								;ea6b	cd 43 e9 	. C . 
	jp exit_and_set_disk_off_timer				;ea6e	c3 8e e2 	. . . 

Sense_disk_interrupt_status:
	ld a,008h									; 00001000b = SENSE INTERRUPT STATUS
	call send_disk_stat							;ea73	cd c5 e9 	. . . 

	ld b,002h									; expect 2 status bytes
	call Read_Disk_status_bytes
	ret

sub_ea7ch:
	call sub_e547h		;ea7c	cd 47 e5 	. G . 
	ret nz			;ea7f	c0 	. 
	ld hl,dir_buffer		; start of FCBs 
	ld de,0x5000+6*0x50		; start on line 7 

list_dir_loop:
	ld a,' '
	cp (hl)							; is the first byte a space (0x20)? (?deleted file?)
	inc hl							; skip to filename.
	jr z,skip_dir_entry				; if space: skip file!
	xor a							; is the name empty?
	cp (hl)
	jr z,skip_dir_entry				; skip file!

	ld bc,8							; print 8 characters of filename
	ldir
	ld a,'.'						; dot
	ld (de),a
	inc de
	ld bc,3							; 3 characters extension
	ldir
	push hl							; source and 
	push de							; line position
	inc hl							; get file flags
	bit 0,(hl)						; bit 0 = ??
	jr nz,add_asterisk
	bit 1,(hl)						; bit 1 = ??
	jr z,no_asterisk
add_asterisk:
	ld a,'*'
	inc de							; add space
	ld (de),a
no_asterisk:
	pop de							; get source and 
	pop hl							; line position back
	ex de,hl
	ld bc,00044h					; add 68: next entry is 68+name+dot+ext = 68+8+1+3 = 80 down
	add hl,bc
	ex de,hl
	jr go_next_entry

skip_dir_entry:
	ld bc,11						; add 11 to data pointer (for filename and extension)
	add hl,bc

go_next_entry:
	ld bc,00014h					; move pointer to next file descriptor (8+3+1 processed = 12 + 20 = 32)
	add hl,bc
	ex de,hl						; screen position in de
	push hl							; save both position 
	push de							; and source
	ld de,056e0h					; max line pos in de
	sbc hl,de						; subtract from current line pos
	pop hl							; source back
	pop de							; orig position back
	jr c,do_next_file				; screen is not full
	ex de,hl						; pointer to HL
	ld bc,004ech					; move pointer to first line, second column
	sbc hl,bc
	ex de,hl
do_next_file:
	ld bc,lfed1h					; end of dir buffer (size is 0x800 / 32 = 64  FCBs)
	push hl
	sbc hl,bc						; has hal reached end of buffer?
	pop hl
	jr c,list_dir_loop				; no, continue
	ret								; done!

print_header_and_footer:
	ld hl,0514dh					; somewhere top of screen
	ld b,10							; index of string ' DIRECTORY     FREE SPACE IN K :'
	call print_string		 
	ld hl, 05730h					; 1st position of line24 on screen
	ld b,11							; index of the string: 11 = 'TURN PAGE ...'
	call print_string

	ld hl,(lf57bh)		;eaec	2a 7b f5 	* { . 
	ld de,lf57dh		;eaef	11 7d f5 	. } . 
	call sub_e57ah		;eaf2	cd 7a e5 	. z . 
	ld a,(lf57fh)		;eaf5	3a 7f f5 	:  . 
	cp 030h		;eaf8	fe 30 	. 0 
	jr z,leaffh		;eafa	28 03 	( . 
	ld (05170h),a		;eafc	32 70 51 	2 p Q 
leaffh:
	ld a,(lf580h)		;eaff	3a 80 f5 	: . . 
	ld (05171h),a		;eb02	32 71 51 	2 q Q 
	ld a,(lf581h)		;eb05	3a 81 f5 	: . . 
	ld (05172h),a		;eb08	32 72 51 	2 r Q 
	ret			;eb0b	c9 	. 
sub_eb0ch:
	ld a,(VIDEO_ram)		;eb0c	3a 00 50 	: . P 
	and 00fh		;eb0f	e6 0f 	. . 
	dec a			;eb11	3d 	= 
	ld hl,Active_drive		;eb12	21 76 f5 	! v . 
	cp (hl)			;eb15	be 	. 
	ld hl,PDOS_flags		;eb16	21 24 f5 	! $ . 
	jr z,leb1fh		;eb19	28 04 	( . 
	res 0,(hl)		;eb1b	cb 86 	. . 
	res 4,(hl)		;eb1d	cb a6 	. . 
leb1fh:
	inc a			;eb1f	3c 	< 
	bit 7,(hl)		;eb20	cb 7e 	. ~ 
	ret z			;eb22	c8 	. 
sub_eb23h:
	and 00fh		;eb23	e6 0f 	. . 
	ld (Disk_flag),a		;eb25	32 0f 69 	2 . i 
	sub 005h		;eb28	d6 05 	. . 
	jr nc,leb30h		;eb2a	30 04 	0 . 
	add a,005h		;eb2c	c6 05 	. . 
	jr leb3dh		;eb2e	18 0d 	. . 
leb30h:
	ld a,00ah		;eb30	3e 0a 	> . 
	ld (Disk_error_code),a		;eb32	32 1e f5 	2 . . 
	ld b,008h		;eb35	06 08 	. . 
	call Print_message		;eb37	cd 97 e6 	. . . 
	jp exit_and_set_disk_off_timer		;eb3a	c3 8e e2 	. . . 


leb3dh:
	call set_active_drive		;eb3d	cd 5f eb 	. _ . 
	dec a			;eb40	3d 	= 
	ld (Active_drive),a		;eb41	32 76 f5 	2 v . 
	push af			;eb44	f5 	. 
	inc a			;eb45	3c 	< 
	ld b,a			;eb46	47 	G 
	ld hl,lf550h		;eb47	21 50 f5 	! P . 
	ld a,(hl)			;eb4a	7e 	~ 
leb4bh:
	rrc a		;eb4b	cb 0f 	. . 
	djnz leb4bh		;eb4d	10 fc 	. . 
	ld hl,lf68dh		;eb4f	21 8d f6 	! . . 
	jr nc,leb57h		;eb52	30 03 	0 . 
	inc hl			;eb54	23 	# 
	inc hl			;eb55	23 	# 
	inc hl			;eb56	23 	# 
leb57h:
	ld de,lf571h		;eb57	11 71 f5 	. q . 
	call copy_3_bytes		;eb5a	cd da e5 	. . . 
	pop af			;eb5d	f1 	. 
	ret			;eb5e	c9 	. 

;
; prep all drive commands for drive # in A
;
;
set_active_drive:
	push af
	call mask_drive_bits				; turn into correct drive number (mask bits)

	ld d,000h							; assume head 0
	ld hl,Drive_head_table				; drives table

	ld e,a								; add drive #
	add hl,de							; 
	or (hl)								; or head-bit into a
	bit 2,a								; test head bit
	jr z,set_drv_and_head_in_commands	; if zero: d (head) remains 0
	inc d								; switch to other side

set_drv_and_head_in_commands:
	ld (SEEK_cmd_hd_drive_select),a
	ld (RECALIBRATE_cmd_hd_drive_select),a
	ld (RW_cmd_hd_drive_select),a
	ld a,d								; get just the head
	ld (RW_cmd_head),a					; store in RW command
	pop af
	ret

;
; dead code?
;
dead_code:
	cp 003h								; drive less than 00000011 ?
	jr c,mask_drive_bits				; yes, leave alone
	xor 003h							; turn ..11 into ..00 

mask_drive_bits:
	and 003h							; only keep bits 0 and 1
	ret

sub_eb88h:
	call Recalibrate_drive		;eb88	cd 39 e7 	. 9 . 
	ld a,(lf575h)		;eb8b	3a 75 f5 	: u . 
	inc a			;eb8e	3c 	< 
	ld (RW_cmd_track),a		;eb8f	32 39 f5 	2 9 . 
	ld hl,(0f662h)		;eb92	2a 62 f6 	* b . 
	xor a			;eb95	af 	. 
	ld (hl),a			;eb96	77 	w 
	inc hl			;eb97	23 	# 
	ld (hl),a			;eb98	77 	w 
	ld hl,(lf660h)		;eb99	2a 60 f6 	* ` . 
	ld (hl),a			;eb9c	77 	w 
	ret			;eb9d	c9 	. 
leb9eh:
	call sub_f447h		;eb9e	cd 47 f4 	. G . 
	jr nc,lebb7h		;eba1	30 14 	0 . 
	ld hl,(0f662h)		;eba3	2a 62 f6 	* b . 
	ex de,hl			;eba6	eb 	. 
	ld a,(lf56dh)		;eba7	3a 6d f5 	: m . 
	call sub_f458h		;ebaa	cd 58 f4 	. X . 
	ex de,hl			;ebad	eb 	. 
	ld (hl),d			;ebae	72 	r 
	dec hl			;ebaf	2b 	+ 
	ld (hl),e			;ebb0	73 	s 
	ld hl,(lf660h)		;ebb1	2a 60 f6 	* ` . 
	dec (hl)			;ebb4	35 	5 
	jr leb9eh		;ebb5	18 e7 	. . 
lebb7h:
	ld hl,(0f662h)		;ebb7	2a 62 f6 	* b . 
	ld a,(lf56dh)		;ebba	3a 6d f5 	: m . 
	ld e,a			;ebbd	5f 	_ 
	ld d,000h		;ebbe	16 00 	. . 
	ex de,hl			;ebc0	eb 	. 
	ld a,(de)			;ebc1	1a 	. 
	add a,l			;ebc2	85 	. 
	ld l,a			;ebc3	6f 	o 
	inc de			;ebc4	13 	. 
	ld a,(de)			;ebc5	1a 	. 
	adc a,h			;ebc6	8c 	. 
	ld h,a			;ebc7	67 	g 
	ld (lf668h),hl		;ebc8	22 68 f6 	" h . 
	ld de,lf666h		;ebcb	11 66 f6 	. f . 
	call sub_f45bh		;ebce	cd 5b f4 	. [ . 
	jr c,lebe5h		;ebd1	38 12 	8 . 
	ld hl,(0f662h)		;ebd3	2a 62 f6 	* b . 
	push hl			;ebd6	e5 	. 
	ld hl,(lf668h)		;ebd7	2a 68 f6 	* h . 
	ex de,hl			;ebda	eb 	. 
	pop hl			;ebdb	e1 	. 
	ld (hl),e			;ebdc	73 	s 
	inc hl			;ebdd	23 	# 
	ld (hl),d			;ebde	72 	r 
	ld hl,(lf660h)		;ebdf	2a 60 f6 	* ` . 
	inc (hl)			;ebe2	34 	4 
	jr lebb7h		;ebe3	18 d2 	. . 
lebe5h:
	ld hl,(lf660h)		;ebe5	2a 60 f6 	* ` . 
	ld a,(lf575h)		;ebe8	3a 75 f5 	: u . 
	add a,(hl)			;ebeb	86 	. 
	inc a			;ebec	3c 	< 
	ld (RW_cmd_track),a		;ebed	32 39 f5 	2 9 . 
	call sub_f447h		;ebf0	cd 47 f4 	. G . 
	ld c,l			;ebf3	4d 	M 
	ld hl,lf555h		;ebf4	21 55 f5 	! U . 
	ld b,000h		;ebf7	06 00 	. . 
	add hl,bc			;ebf9	09 	. 
	ld a,(hl)			;ebfa	7e 	~ 
	ld (RW_cmd_sector),a		;ebfb	32 3b f5 	2 ; . 
	ret			;ebfe	c9 	. 
sub_ebffh:
	ld hl,(lf56fh)		;ebff	2a 6f f5 	* o . 
	ld c,l			;ec02	4d 	M 
	ld hl,lf665h		;ec03	21 65 f6 	! e . 
	call sub_f43eh		;ec06	cd 3e f4 	. > . 
	add a,010h		;ec09	c6 10 	. . 
	ld c,a			;ec0b	4f 	O 
	ld b,000h		;ec0c	06 00 	. . 
	ld hl,(0f579h)		;ec0e	2a 79 f5 	* y . 
sub_ec11h:
	add hl,bc			;ec11	09 	. 
sub_ec12h:
	ld l,(hl)			;ec12	6e 	n 
sub_ec13h:
	ld h,000h		;ec13	26 00 	& . 
	ld (lf666h),hl		;ec15	22 66 f6 	" f . 
	ret			;ec18	c9 	. 
sub_ec19h:
	ld hl,(lf56fh)		;ec19	2a 6f f5 	* o . 
	ld c,l			;ec1c	4d 	M 
	ld hl,lf666h		;ec1d	21 66 f6 	! f . 
	call sub_f435h		;ec20	cd 35 f4 	. 5 . 
	ld a,(lf570h)		;ec23	3a 70 f5 	: p . 
	push hl			;ec26	e5 	. 
	ld hl,lf665h		;ec27	21 65 f6 	! e . 
	and (hl)			;ec2a	a6 	. 
	pop hl			;ec2b	e1 	. 
	ld e,a			;ec2c	5f 	_ 
	ld d,000h		;ec2d	16 00 	. . 
	ld a,e			;ec2f	7b 	{ 
	or l			;ec30	b5 	. 
sub_ec31h:
	ld l,a			;ec31	6f 	o 
	ld a,d			;ec32	7a 	z 
sub_ec33h:
	or h			;ec33	b4 	. 
	ld h,a			;ec34	67 	g 
	ld (lf666h),hl		;ec35	22 66 f6 	" f . 
	ret			;ec38	c9 	. 
sub_ec39h:
	ld bc,0x0020		;ec39	01 20 00 	.   . 
	ld hl,(0f579h)		;ec3c	2a 79 f5 	* y . 
	add hl,bc			;ec3f	09 	. 
	ld a,(hl)			;ec40	7e 	~ 
	ld (lf665h),a		;ec41	32 65 f6 	2 e . 
	ld bc,0000fh		;ec44	01 0f 00 	. . . 
	ld hl,(0f579h)		;ec47	2a 79 f5 	* y . 
	add hl,bc			;ec4a	09 	. 
	ld a,(hl)			;ec4b	7e 	~ 
	ld (0f664h),a		;ec4c	32 64 f6 	2 d . 
	ret			;ec4f	c9 	. 
sub_ec50h:
	ld a,(lf665h)		;ec50	3a 65 f6 	: e . 
sub_ec53h:
	inc a			;ec53	3c 	< 
	ld bc,0x0020		;ec54	01 20 00 	.   . 
	ld hl,(0f579h)		;ec57	2a 79 f5 	* y . 
	add hl,bc			;ec5a	09 	. 
	ld (hl),a			;ec5b	77 	w 
	ld bc,0000fh		;ec5c	01 0f 00 	. . . 
	ld hl,(0f579h)		;ec5f	2a 79 f5 	* y . 
	add hl,bc			;ec62	09 	. 
	ld a,(0f664h)		;ec63	3a 64 f6 	: d . 
	ld (hl),a			;ec66	77 	w 
	ret			;ec67	c9 	. 
sub_ec68h:
	ld a,(lf585h)		;ec68	3a 85 f5 	: . . 
	and 0fch		;ec6b	e6 fc 	. . 
	rra			;ec6d	1f 	. 
	rra			;ec6e	1f 	. 
	rra			;ec6f	1f 	. 
	ld (lf586h),a		;ec70	32 86 f5 	2 . . 
sub_ec73h:
	ld c,a			;ec73	4f 	O 
	ld b,000h		;ec74	06 00 	. . 
	ld h,b			;ec76	60 	` 
	ld l,c			;ec77	69 	i 
	ld (lf666h),hl		;ec78	22 66 f6 	" f . 
	call leb9eh		;ec7b	cd 9e eb 	. . . 
	ret			;ec7e	c9 	. 
sub_ec7fh:
	xor a			;ec7f	af 	. 
	ld (lf66dh),a		;ec80	32 6d f6 	2 m . 
	ld (lf66ch),a		;ec83	32 6c f6 	2 l . 
lec86h:
	ld a,(lf56bh)		;ec86	3a 6b f5 	: k . 
	dec a			;ec89	3d 	= 
	ld hl,lf66ch		;ec8a	21 6c f6 	! l . 
	cp (hl)			;ec8d	be 	. 
	jr c,leca7h		;ec8e	38 17 	8 . 
	ld hl,(lf66ch)		;ec90	2a 6c f6 	* l . 
	ld h,000h		;ec93	26 00 	& . 
	ex de,hl			;ec95	eb 	. 
	ld hl,(lf587h)		;ec96	2a 87 f5 	* . . 
	add hl,de			;ec99	19 	. 
	ld a,(lf66dh)		;ec9a	3a 6d f6 	: m . 
	add a,(hl)			;ec9d	86 	. 
	ld (lf66dh),a		;ec9e	32 6d f6 	2 m . 
	ld hl,lf66ch		;eca1	21 6c f6 	! l . 
	inc (hl)			;eca4	34 	4 
	jr nz,lec86h		;eca5	20 df 	  . 
leca7h:
	ld a,(lf66dh)		;eca7	3a 6d f6 	: m . 
	ret			;ecaa	c9 	. 
sub_ecabh:
	ld hl,lf66eh		;ecab	21 6e f6 	! n . 
	ld (hl),c			;ecae	71 	q 
	ld a,(Active_drive)		;ecaf	3a 76 f5 	: v . 
	inc a			;ecb2	3c 	< 
	ld c,a			;ecb3	4f 	O 
	ld a,001h		;ecb4	3e 01 	> . 
	call sub_f42ah		;ecb6	cd 2a f4 	. * . 
	rrca			;ecb9	0f 	. 
	ld hl,lf66eh		;ecba	21 6e f6 	! n . 
	or (hl)			;ecbd	b6 	. 
	ret			;ecbe	c9 	. 
sub_ecbfh:
	ld a,(PDOS_flags)		;ecbf	3a 24 f5 	: $ . 
	or a			;ecc2	b7 	. 
	bit 7,a		;ecc3	cb 7f 	.  
	ld a,000h		;ecc5	3e 00 	> . 
	ret nz			;ecc7	c0 	. 
	ld a,(Active_drive)		;ecc8	3a 76 f5 	: v . 
	inc a			;eccb	3c 	< 
	ld c,a			;eccc	4f 	O 
	ld hl,lf60eh		;eccd	21 0e f6 	! . . 
	call sub_f42fh		;ecd0	cd 2f f4 	. / . 
	rlca			;ecd3	07 	. 
	ret			;ecd4	c9 	. 

sub_ecd5h:
	ld hl,(lf60eh)		;ecd5	2a 0e f6 	* . . 
	ld c,l			;ecd8	4d 	M 
	call sub_ecabh		;ecd9	cd ab ec 	. . . 
	ld (lf60eh),a		;ecdc	32 0e f6 	2 . . 
	ret			;ecdf	c9 	. 
sub_ece0h:
	call sub_ecbfh		;ece0	cd bf ec 	. . . 
	rra			;ece3	1f 	. 
	ret nc			;ece4	d0 	. 
	ld a,002h		;ece5	3e 02 	> . 
	jp le986h		;ece7	c3 86 e9 	. . . 
sub_eceah:
	ld hl,lf66eh+1		;ecea	21 6f f6 	! o . 
	ld (hl),c			;eced	71 	q 
	ld a,(lf586h)		;ecee	3a 86 f5 	: . . 
	cp 010h		;ecf1	fe 10 	. . 
	ret nc			;ecf3	d0 	. 
	ld a,(lf66eh+1)		;ecf4	3a 6f f6 	: o . 
	rra			;ecf7	1f 	. 
	jr nc,led09h		;ecf8	30 0f 	0 . 
	call sub_ec7fh		;ecfa	cd 7f ec 	.  . 
	ld hl,(lf586h)		;ecfd	2a 86 f5 	* . . 
	ld h,000h		;ed00	26 00 	& . 
	ex de,hl			;ed02	eb 	. 
	ld hl,(lf64fh)		;ed03	2a 4f f6 	* O . 
	add hl,de			;ed06	19 	. 
	ld (hl),a			;ed07	77 	w 
	ret			;ed08	c9 	. 
led09h:
	ld hl,(lf586h)		;ed09	2a 86 f5 	* . . 
	ld h,000h		;ed0c	26 00 	& . 
	ex de,hl			;ed0e	eb 	. 
	ld hl,(lf64fh)		;ed0f	2a 4f f6 	* O . 
	add hl,de			;ed12	19 	. 
sub_ed13h:
	push hl			;ed13	e5 	. 
	call sub_ec7fh		;ed14	cd 7f ec 	.  . 
	pop hl			;ed17	e1 	. 
	cp (hl)			;ed18	be 	. 
	ret z			;ed19	c8 	. 
	call sub_ecd5h		;ed1a	cd d5 ec 	. . . 
	ret			;ed1d	c9 	. 
sub_ed1eh:
	ld c,001h		;ed1e	0e 01 	. . 
	call sub_eceah		;ed20	cd ea ec 	. . . 
	call sub_f130h		;ed23	cd 30 f1 	. 0 . 
	call sub_e56bh		;ed26	cd 6b e5 	. k . 
	jr z,led32h		;ed29	28 07 	( . 
	call sub_e526h		;ed2b	cd 26 e5 	. & . 
	call sub_e831h		;ed2e	cd 31 e8 	. 1 . 
	ret			;ed31	c9 	. 
led32h:
	ld a,001h		;ed32	3e 01 	> . 
	ld (lf56dh),a		;ed34	32 6d f5 	2 m . 
	call sub_e84fh		;ed37	cd 4f e8 	. O . 
	ret			;ed3a	c9 	. 
sub_ed3bh:
	ld hl,lf66eh+2		;ed3b	21 70 f6 	! p . 
	ld (hl),c			;ed3e	71 	q 
	ld a,(lf585h)		;ed3f	3a 85 f5 	: . . 
	inc a			;ed42	3c 	< 
	ld (lf585h),a		;ed43	32 85 f5 	2 . . 
	ld c,a			;ed46	4f 	O 
	ld a,(0f56eh)		;ed47	3a 6e f5 	: n . 
	cp c			;ed4a	b9 	. 
	jr nz,led53h		;ed4b	20 06 	  . 
	ld hl,lf585h		;ed4d	21 85 f5 	! . . 
	ld (hl),0ffh		;ed50	36 ff 	6 . 
	ret			;ed52	c9 	. 
led53h:
	ld a,(lf585h)		;ed53	3a 85 f5 	: . . 
	and 007h		;ed56	e6 07 	. . 
	add a,a			;ed58	87 	. 
	add a,a			;ed59	87 	. 
	add a,a			;ed5a	87 	. 
	add a,a			;ed5b	87 	. 
	add a,a			;ed5c	87 	. 
	ld (lf584h),a		;ed5d	32 84 f5 	2 . . 
	cp 000h		;ed60	fe 00 	. . 
	ret nz			;ed62	c0 	. 
	call sub_ec68h		;ed63	cd 68 ec 	. h . 
	call sub_e56bh		;ed66	cd 6b e5 	. k . 
	jr nz,led70h		;ed69	20 05 	  . 
	call sub_e8b3h		;ed6b	cd b3 e8 	. . . 
	jr led73h		;ed6e	18 03 	. . 
led70h:
	call sub_ed7bh		;ed70	cd 7b ed 	. { . 
led73h:
	ld hl,(lf66eh+2)		;ed73	2a 70 f6 	* p . 
	ld c,l			;ed76	4d 	M 
	call sub_eceah		;ed77	cd ea ec 	. . . 
	ret			;ed7a	c9 	. 
sub_ed7bh:
	ld hl,(lf587h)		;ed7b	2a 87 f5 	* . . 
	inc h			;ed7e	24 	$ 
	jr led91h		;ed7f	18 10 	. . 
sub_ed81h:
	call sub_e526h		;ed81	cd 26 e5 	. & . 
	ld hl,PDOS_flags		;ed84	21 24 f5 	! $ . 
	bit 0,(hl)		;ed87	cb 46 	. F 
	jr nz,led8eh		;ed89	20 03 	  . 
	call sub_e774h		;ed8b	cd 74 e7 	. t . 
led8eh:
	ld hl,lf5d5h		;ed8e	21 d5 f5 	! . . 
led91h:
	ld (lf587h),hl		;ed91	22 87 f5 	" . . 
	ret			;ed94	c9 	. 
sub_ed95h:
	ld hl,lf671h		;ed95	21 71 f6 	! q . 
	ld (hl),c			;ed98	71 	q 
	ld a,(lf671h)		;ed99	3a 71 f6 	: q . 
	and 0fch		;ed9c	e6 fc 	. . 
	rra			;ed9e	1f 	. 
	rra			;ed9f	1f 	. 
	rra			;eda0	1f 	. 
	ld c,a			;eda1	4f 	O 
	ld b,000h		;eda2	06 00 	. . 
	ld hl,(0f60ch)		;eda4	2a 0c f6 	* . . 
	add hl,bc			;eda7	09 	. 
	ld a,(lf671h)		;eda8	3a 71 f6 	: q . 
	and 007h		;edab	e6 07 	. . 
	inc a			;edad	3c 	< 
	ld c,a			;edae	4f 	O 
	call sub_f429h		;edaf	cd 29 f4 	. ) . 
	ret			;edb2	c9 	. 
sub_edb3h:
	ld hl,lf671h+2		;edb3	21 73 f6 	! s . 
	ld (hl),e			;edb6	73 	s 
	dec hl			;edb7	2b 	+ 
	ld (hl),c			;edb8	71 	q 
	ld hl,(lf671h+1)		;edb9	2a 72 f6 	* r . 
	ld c,l			;edbc	4d 	M 
	call sub_ed95h		;edbd	cd 95 ed 	. . . 
	and 0feh		;edc0	e6 fe 	. . 
	ld hl,lf671h+2		;edc2	21 73 f6 	! s . 
	or (hl)			;edc5	b6 	. 
	push af			;edc6	f5 	. 
	ld a,007h		;edc7	3e 07 	> . 
	dec hl			;edc9	2b 	+ 
	and (hl)			;edca	a6 	. 
	inc a			;edcb	3c 	< 
	ld c,a			;edcc	4f 	O 
	pop af			;edcd	f1 	. 
	call sub_f430h		;edce	cd 30 f4 	. 0 . 
	push af			;edd1	f5 	. 
	ld a,(hl)			;edd2	7e 	~ 
	and 0fch		;edd3	e6 fc 	. . 
	rra			;edd5	1f 	. 
	rra			;edd6	1f 	. 
	rra			;edd7	1f 	. 
	ld c,a			;edd8	4f 	O 
	ld b,000h		;edd9	06 00 	. . 
	ld hl,(0f60ch)		;eddb	2a 0c f6 	* . . 
	add hl,bc			;edde	09 	. 
	pop bc			;eddf	c1 	. 
	ld c,b			;ede0	48 	H 
	ld (hl),c			;ede1	71 	q 
	ret			;ede2	c9 	. 
sub_ede3h:
	ld hl,lf674h		;ede3	21 74 f6 	! t . 
	ld (hl),c			;ede6	71 	q 
	ld a,(lf584h)		;ede7	3a 84 f5 	: . . 
	add a,010h		;edea	c6 10 	. . 
	ld (lf674h+1),a		;edec	32 75 f6 	2 u . 
ledefh:
	ld a,(lf584h)		;edef	3a 84 f5 	: . . 
	add a,01fh		;edf2	c6 1f 	. . 
	ld hl,lf674h+1		;edf4	21 75 f6 	! u . 
	cp (hl)			;edf7	be 	. 
	ret c			;edf8	d8 	. 
	ld hl,(lf674h+1)		;edf9	2a 75 f6 	* u . 
	ld h,000h		;edfc	26 00 	& . 
	ex de,hl			;edfe	eb 	. 
	ld hl,(lf587h)		;edff	2a 87 f5 	* . . 
	add hl,de			;ee02	19 	. 
	ld a,(lf573h)		;ee03	3a 73 f5 	: s . 
	ld c,a			;ee06	4f 	O 
	inc c			;ee07	0c 	. 
	ld a,(hl)			;ee08	7e 	~ 
	cp c			;ee09	b9 	. 
	jr nc,lee1eh		;ee0a	30 12 	0 . 
	ld (lf674h+2),a		;ee0c	32 76 f6 	2 v . 
	cp 000h		;ee0f	fe 00 	. . 
sub_ee11h:
	jr z,lee1eh		;ee11	28 0b 	( . 
sub_ee13h:
	ld hl,(lf674h+2)		;ee13	2a 76 f6 	* v . 
	ld c,l			;ee16	4d 	M 
	ld hl,(lf674h)		;ee17	2a 74 f6 	* t . 
	ex de,hl			;ee1a	eb 	. 
	call sub_edb3h		;ee1b	cd b3 ed 	. . . 
lee1eh:
	ld hl,lf674h+1		;ee1e	21 75 f6 	! u . 
	inc (hl)			;ee21	34 	4 
	jr nz,ledefh		;ee22	20 cb 	  . 
	ret			;ee24	c9 	. 
sub_ee25h:
	ld hl,lf582h		;ee25	21 82 f5 	! . . 
	ld (hl),000h		;ee28	36 00 	6 . 
	ld hl,lf60eh		;ee2a	21 0e f6 	! . . 
	ld (hl),000h		;ee2d	36 00 	6 . 
	ld hl,(0f60ch)		;ee2f	2a 0c f6 	* . . 
	ld a,(lf574h)		;ee32	3a 74 f5 	: t . 
	ld (hl),a			;ee35	77 	w 
	ld hl,lf677h		;ee36	21 77 f6 	! w . 
	ld (hl),001h		;ee39	36 01 	6 . 
lee3bh:
	ld a,019h		;ee3b	3e 19 	> . 
	ld hl,lf677h		;ee3d	21 77 f6 	! w . 
	cp (hl)			;ee40	be 	. 
	jr c,lee55h		;ee41	38 12 	8 . 
	ld hl,(lf677h)		;ee43	2a 77 f6 	* w . 
	ld h,000h		;ee46	26 00 	& . 
	ex de,hl			;ee48	eb 	. 
	ld hl,(0f60ch)		;ee49	2a 0c f6 	* . . 
	add hl,de			;ee4c	19 	. 
	ld (hl),000h		;ee4d	36 00 	6 . 
	ld hl,lf677h		;ee4f	21 77 f6 	! w . 
sub_ee52h:
	inc (hl)			;ee52	34 	4 
sub_ee53h:
	jr nz,lee3bh		;ee53	20 e6 	  . 
lee55h:
	call sub_eb88h		;ee55	cd 88 eb 	. . . 
	ld a,(Retry_counter+1)		;ee58	3a 1d f5 	: . . 
	cp 028h		;ee5b	fe 28 	. ( 
	ret z			;ee5d	c8 	. 
	ld hl,lf585h		;ee5e	21 85 f5 	! . . 
	ld (hl),0ffh		;ee61	36 ff 	6 . 
lee63h:
	call sub_e56bh		;ee63	cd 6b e5 	. k . 
	jr z,lee6eh		;ee66	28 06 	( . 
	call sub_ed81h		;ee68	cd 81 ed 	. . . 
	call sub_e51fh		;ee6b	cd 1f e5 	. . . 
lee6eh:
	ld c,001h		;ee6e	0e 01 	. . 
sub_ee70h:
	call sub_ed3bh		;ee70	cd 3b ed 	. ; . 
sub_ee73h:
	call sub_f037h		;ee73	cd 37 f0 	. 7 . 
	ret z			;ee76	c8 	. 
	ld hl,(lf584h)		;ee77	2a 84 f5 	* . . 
	ld h,000h		;ee7a	26 00 	& . 
	ex de,hl			;ee7c	eb 	. 
	ld hl,(lf587h)		;ee7d	2a 87 f5 	* . . 
	add hl,de			;ee80	19 	. 
	ld a,(hl)			;ee81	7e 	~ 
	cp 0e5h		;ee82	fe e5 	. . 
	jr z,leeabh		;ee84	28 25 	( % 
	ld hl,(lf584h)		;ee86	2a 84 f5 	* . . 
	ld h,000h		;ee89	26 00 	& . 
	ld bc,CST_WCDON		;ee8b	01 01 00 	. . . 
	add hl,bc			;ee8e	09 	. 
	ex de,hl			;ee8f	eb 	. 
	ld hl,(lf587h)		;ee90	2a 87 f5 	* . . 
	add hl,de			;ee93	19 	. 
	ld a,(hl)			;ee94	7e 	~ 
	sub 024h		;ee95	d6 24 	. $ 
	sub 001h		;ee97	d6 01 	. . 
	sbc a,a			;ee99	9f 	. 
	ld hl,lf582h		;ee9a	21 82 f5 	! . . 
	or (hl)			;ee9d	b6 	. 
	ld (hl),a			;ee9e	77 	w 
	ld c,001h		;ee9f	0e 01 	. . 
	call sub_ede3h		;eea1	cd e3 ed 	. . . 
	ld a,(lf585h)		;eea4	3a 85 f5 	: . . 
	cp 03fh		;eea7	fe 3f 	. ? 
	jr z,lee63h		;eea9	28 b8 	( . 
leeabh:
	jr lee6eh		;eeab	18 c1 	. . 
sub_eeadh:
	ld hl,(lf679h)		;eead	2a 79 f6 	* y . 
	ld (0f579h),hl		;eeb0	22 79 f5 	" y . 
leeb3h:
	call sub_e56bh		;eeb3	cd 6b e5 	. k . 
	jr z,leebbh		;eeb6	28 03 	( . 
	call sub_ed81h		;eeb8	cd 81 ed 	. . . 
leebbh:
	ld c,000h		;eebb	0e 00 	. . 
	call sub_ed3bh		;eebd	cd 3b ed 	. ; . 
	call sub_f037h		;eec0	cd 37 f0 	. 7 . 
	ld (lf582h),a		;eec3	32 82 f5 	2 . . 
	ret z			;eec6	c8 	. 
	ld a,(lf678h)		;eec7	3a 78 f6 	: x . 
	cp 002h		;eeca	fe 02 	. . 
	jr z,leed7h		;eecc	28 09 	( . 
	xor a			;eece	af 	. 
	call sub_ef31h		;eecf	cd 31 ef 	. 1 . 
	inc hl			;eed2	23 	# 
	ld a,(hl)			;eed3	7e 	~ 
	or a			;eed4	b7 	. 
	jr z,lef28h		;eed5	28 51 	( Q 
leed7h:
	ld hl,lf67bh		;eed7	21 7b f6 	! { . 
	ld (hl),000h		;eeda	36 00 	6 . 
leedch:
	ld hl,lf678h		;eedc	21 78 f6 	! x . 
	ld a,(lf67bh)		;eedf	3a 7b f6 	: { . 
	sub (hl)			;eee2	96 	. 
	sbc a,a			;eee3	9f 	. 
	ld hl,(lf67bh)		;eee4	2a 7b f6 	* { . 
	ld h,000h		;eee7	26 00 	& . 
	ex de,hl			;eee9	eb 	. 
	ld hl,(0f579h)		;eeea	2a 79 f5 	* y . 
	add hl,de			;eeed	19 	. 
	push af			;eeee	f5 	. 
	ld a,(hl)			;eeef	7e 	~ 
	ld (lf67ch),a		;eef0	32 7c f6 	2 | . 
	ld a,(lf67bh)		;eef3	3a 7b f6 	: { . 
	push hl			;eef6	e5 	. 
	call sub_ef31h		;eef7	cd 31 ef 	. 1 . 
	ld a,0f3h		;eefa	3e f3 	> . 
	cp (hl)			;eefc	be 	. 
	jr nz,lef02h		;eefd	20 03 	  . 
	ld hl,lf569h		;eeff	21 69 f5 	! i . 
lef02h:
	pop bc			;ef02	c1 	. 
	ld a,(bc)			;ef03	0a 	. 
	sub (hl)			;ef04	96 	. 
	sub 001h		;ef05	d6 01 	. . 
	sbc a,a			;ef07	9f 	. 
	push af			;ef08	f5 	. 
	ld a,(lf67ch)		;ef09	3a 7c f6 	: | . 
	sub 03fh		;ef0c	d6 3f 	. ? 
	sub 001h		;ef0e	d6 01 	. . 
	sbc a,a			;ef10	9f 	. 
	pop bc			;ef11	c1 	. 
	ld c,b			;ef12	48 	H 
	or c			;ef13	b1 	. 
	pop bc			;ef14	c1 	. 
	ld c,b			;ef15	48 	H 
	and c			;ef16	a1 	. 
	rra			;ef17	1f 	. 
	jr nc,lef20h		;ef18	30 06 	0 . 
	ld hl,lf67bh		;ef1a	21 7b f6 	! { . 
	inc (hl)			;ef1d	34 	4 
	jr leedch		;ef1e	18 bc 	. . 
lef20h:
	ld hl,lf678h		;ef20	21 78 f6 	! x . 
	ld a,(lf67bh)		;ef23	3a 7b f6 	: { . 
	cp (hl)			;ef26	be 	. 
	ret z			;ef27	c8 	. 
lef28h:
	ld a,(lf585h)		;ef28	3a 85 f5 	: . . 
	cp 03fh		;ef2b	fe 3f 	. ? 
	jr z,leeb3h		;ef2d	28 84 	( . 
	jr leebbh		;ef2f	18 8a 	. . 
sub_ef31h:
	ld hl,lf584h		;ef31	21 84 f5 	! . . 
	add a,(hl)			;ef34	86 	. 
	ld c,a			;ef35	4f 	O 
	ld b,000h		;ef36	06 00 	. . 
	ld hl,(lf587h)		;ef38	2a 87 f5 	* . . 
	add hl,bc			;ef3b	09 	. 
	ret			;ef3c	c9 	. 
sub_ef3dh:
	ld hl,lf67dh		;ef3d	21 7d f6 	! } . 
	ld (hl),c			;ef40	71 	q 
	ld a,c			;ef41	79 	y 
	ld (lf678h),a		;ef42	32 78 f6 	2 x . 
	ld hl,(0f579h)		;ef45	2a 79 f5 	* y . 
	ld (lf679h),hl		;ef48	22 79 f6 	" y . 
	ld hl,lf585h		;ef4b	21 85 f5 	! . . 
	ld (hl),0ffh		;ef4e	36 ff 	6 . 
	call sub_eb88h		;ef50	cd 88 eb 	. . . 
	call sub_eeadh		;ef53	cd ad ee 	. . . 
	ret			;ef56	c9 	. 
sub_ef57h:
	call sub_ece0h		;ef57	cd e0 ec 	. . . 
	ld c,00ch		;ef5a	0e 0c 	. . 
	call sub_ef3dh		;ef5c	cd 3d ef 	. = . 
lef5fh:
	call sub_f037h		;ef5f	cd 37 f0 	. 7 . 
	ret z			;ef62	c8 	. 
	ld c,000h		;ef63	0e 00 	. . 
	call sub_ede3h		;ef65	cd e3 ed 	. . . 
	call sub_f015h		;ef68	cd 15 f0 	. . . 
	xor a			;ef6b	af 	. 
	ld (hl),a			;ef6c	77 	w 
	push hl			;ef6d	e5 	. 
	pop de			;ef6e	d1 	. 
	inc de			;ef6f	13 	. 
	ld bc,0001fh		;ef70	01 1f 00 	. . . 
sub_ef73h:
	ldir		;ef73	ed b0 	. . 
	call sub_ed1eh		;ef75	cd 1e ed 	. . . 
	call leebbh		;ef78	cd bb ee 	. . . 
	jr lef5fh		;ef7b	18 e2 	. . 
sub_ef7dh:
	ld hl,lf681h		;ef7d	21 81 f6 	! . . 
	ld (hl),c			;ef80	71 	q 
	ld a,c			;ef81	79 	y 
	ld (lf682h),a		;ef82	32 82 f6 	2 . . 
lef85h:
	ld hl,lf573h		;ef85	21 73 f5 	! s . 
	ld a,(lf682h)		;ef88	3a 82 f6 	: . . 
	sub (hl)			;ef8b	96 	. 
	sbc a,a			;ef8c	9f 	. 
	push af			;ef8d	f5 	. 
	ld a,000h		;ef8e	3e 00 	> . 
	ld hl,lf681h		;ef90	21 81 f6 	! . . 
	sub (hl)			;ef93	96 	. 
	sbc a,a			;ef94	9f 	. 
	pop bc			;ef95	c1 	. 
	ld c,b			;ef96	48 	H 
	or c			;ef97	b1 	. 
	rra			;ef98	1f 	. 
	jr nc,lefd1h		;ef99	30 36 	0 6 
	ld a,000h		;ef9b	3e 00 	> . 
	sub (hl)			;ef9d	96 	. 
	sbc a,a			;ef9e	9f 	. 
	and 001h		;ef9f	e6 01 	. . 
	push af			;efa1	f5 	. 
	ld a,(hl)			;efa2	7e 	~ 
	pop bc			;efa3	c1 	. 
	ld c,b			;efa4	48 	H 
	sub c			;efa5	91 	. 
	ld (hl),a			;efa6	77 	w 
	inc hl			;efa7	23 	# 
	ld a,(hl)			;efa8	7e 	~ 
	ld hl,lf573h		;efa9	21 73 f5 	! s . 
	sub (hl)			;efac	96 	. 
	sbc a,a			;efad	9f 	. 
	and 001h		;efae	e6 01 	. . 
	ld hl,lf682h		;efb0	21 82 f6 	! . . 
	add a,(hl)			;efb3	86 	. 
	ld (hl),a			;efb4	77 	w 
	ld hl,(lf682h)		;efb5	2a 82 f6 	* . . 
	ld c,l			;efb8	4d 	M 
	call sub_ed95h		;efb9	cd 95 ed 	. . . 
	rra			;efbc	1f 	. 
	jr c,lefc3h		;efbd	38 04 	8 . 
	ld a,(lf682h)		;efbf	3a 82 f6 	: . . 
	ret			;efc2	c9 	. 
lefc3h:
	ld hl,(lf681h)		;efc3	2a 81 f6 	* . . 
	ld c,l			;efc6	4d 	M 
	call sub_ed95h		;efc7	cd 95 ed 	. . . 
	rra			;efca	1f 	. 
	jr c,lef85h		;efcb	38 b8 	8 . 
	ld a,(lf681h)		;efcd	3a 81 f6 	: . . 
	ret			;efd0	c9 	. 
lefd1h:
	ld a,000h		;efd1	3e 00 	> . 
	ret			;efd3	c9 	. 
sub_efd4h:
	ld hl,lf684h		;efd4	21 84 f6 	! . . 
	ld (hl),e			;efd7	73 	s 
	dec hl			;efd8	2b 	+ 
	ld (hl),c			;efd9	71 	q 
lefdah:
	ld a,(lf684h)		;efda	3a 84 f6 	: . . 
	dec a			;efdd	3d 	= 
	ld (lf684h),a		;efde	32 84 f6 	2 . . 
	cp 0ffh		;efe1	fe ff 	. . 
	jr z,lf007h		;efe3	28 22 	( " 
	ld a,(lf684h)		;efe5	3a 84 f6 	: . . 
	ld hl,lf683h		;efe8	21 83 f6 	! . . 
	add a,(hl)			;efeb	86 	. 
	ld c,a			;efec	4f 	O 
	ld b,000h		;efed	06 00 	. . 
	ld hl,(0f579h)		;efef	2a 79 f5 	* y . 
	add hl,bc			;eff2	09 	. 
	ld a,(lf584h)		;eff3	3a 84 f5 	: . . 
	push hl			;eff6	e5 	. 
	ld hl,lf684h		;eff7	21 84 f6 	! . . 
	add a,(hl)			;effa	86 	. 
	ld c,a			;effb	4f 	O 
	ld b,000h		;effc	06 00 	. . 
	ld hl,(lf587h)		;effe	2a 87 f5 	* . . 
	add hl,bc			;f001	09 	. 
	pop bc			;f002	c1 	. 
	ld a,(bc)			;f003	0a 	. 
	ld (hl),a			;f004	77 	w 
	jr lefdah		;f005	18 d3 	. . 
lf007h:
	call sub_f015h		;f007	cd 15 f0 	. . . 
	ld a,c			;f00a	79 	y 
	bit 1,a		;f00b	cb 4f 	. O 
	ret nz			;f00d	c0 	. 
	call sub_ec68h		;f00e	cd 68 ec 	. h . 
	call sub_ed1eh		;f011	cd 1e ed 	. . . 
	ret			;f014	c9 	. 
sub_f015h:
	ld hl,(lf584h)		;f015	2a 84 f5 	* . . 
	push de			;f018	d5 	. 
	ld h,000h		;f019	26 00 	& . 
	ex de,hl			;f01b	eb 	. 
	ld hl,(lf587h)		;f01c	2a 87 f5 	* . . 
	add hl,de			;f01f	19 	. 
	pop de			;f020	d1 	. 
	ld c,000h		;f021	0e 00 	. . 
	ld a,(Retry_counter+1)		;f023	3a 1d f5 	: . . 
	cp 034h		;f026	fe 34 	. 4 
	ret z			;f028	c8 	. 
	cp 03ah		;f029	fe 3a 	. : 
	ret z			;f02b	c8 	. 
	push hl			;f02c	e5 	. 
	call sub_e65dh		;f02d	cd 5d e6 	. ] . 
	pop hl			;f030	e1 	. 
	ret			;f031	c9 	. 
sub_f032h:
	ld c,00dh		;f032	0e 0d 	. . 
sub_f034h:
	call sub_ef3dh		;f034	cd 3d ef 	. = . 
sub_f037h:
	ld a,(lf585h)		;f037	3a 85 f5 	: . . 
	cp 0ffh		;f03a	fe ff 	. . 
	ret			;f03c	c9 	. 
sub_f03dh:
	ld e,020h		;f03d	1e 20 	.   
	ld c,000h		;f03f	0e 00 	. . 
	call sub_efd4h		;f041	cd d4 ef 	. . . 
	ret			;f044	c9 	. 
sub_f045h:
	call sub_ece0h		;f045	cd e0 ec 	. . . 
	ld c,00ch		;f048	0e 0c 	. . 
	call sub_ef3dh		;f04a	cd 3d ef 	. = . 
	ld hl,(0f579h)		;f04d	2a 79 f5 	* y . 
	ld bc,0x0010		;f050	01 10 00 	. . . 
	push hl			;f053	e5 	. 
	add hl,bc			;f054	09 	. 
	pop de			;f055	d1 	. 
	ld a,(de)			;f056	1a 	. 
	ld (hl),a			;f057	77 	w 
lf058h:
	call sub_f037h		;f058	cd 37 f0 	. 7 . 
	ret z			;f05b	c8 	. 
	ld e,00ch		;f05c	1e 0c 	. . 
	ld c,010h		;f05e	0e 10 	. . 
	call sub_efd4h		;f060	cd d4 ef 	. . . 
	call sub_eeadh		;f063	cd ad ee 	. . . 
	jr lf058h		;f066	18 f0 	. . 
sub_f068h:
	call sub_f032h		;f068	cd 32 f0 	. 2 . 
	ret z			;f06b	c8 	. 
	ld hl,lf685h		;f06c	21 85 f6 	! . . 
	ld (hl),00dh		;f06f	36 0d 	6 . 
lf071h:
	ld a,01fh		;f071	3e 1f 	> . 
	ld hl,lf685h		;f073	21 85 f6 	! . . 
	cp (hl)			;f076	be 	. 
	ret c			;f077	d8 	. 
	ld a,(lf685h)		;f078	3a 85 f6 	: . . 
	ld hl,lf584h		;f07b	21 84 f5 	! . . 
	add a,(hl)			;f07e	86 	. 
	ld c,a			;f07f	4f 	O 
	ld b,000h		;f080	06 00 	. . 
	ld hl,(lf587h)		;f082	2a 87 f5 	* . . 
	add hl,bc			;f085	09 	. 
	push hl			;f086	e5 	. 
	ld hl,(lf685h)		;f087	2a 85 f6 	* . . 
	ld h,000h		;f08a	26 00 	& . 
	ex de,hl			;f08c	eb 	. 
	ld hl,(0f579h)		;f08d	2a 79 f5 	* y . 
	add hl,de			;f090	19 	. 
	pop bc			;f091	c1 	. 
	ld a,(bc)			;f092	0a 	. 
	ld (hl),a			;f093	77 	w 
	ld hl,lf685h		;f094	21 85 f6 	! . . 
	inc (hl)			;f097	34 	4 
	jr nz,lf071h		;f098	20 d7 	  . 
	ret			;f09a	c9 	. 
sub_f09bh:
	ld hl,lf582h		;f09b	21 82 f5 	! . . 
	ld (hl),000h		;f09e	36 00 	6 . 
	call sub_ecbfh		;f0a0	cd bf ec 	. . . 
	rra			;f0a3	1f 	. 
	ret c			;f0a4	d8 	. 
	call sub_f032h		;f0a5	cd 32 f0 	. 2 . 
	ret z			;f0a8	c8 	. 
	call sub_f03dh		;f0a9	cd 3d f0 	. = . 
	ret			;f0ac	c9 	. 
sub_f0adh:
	call sub_ece0h		;f0ad	cd e0 ec 	. . . 
	ld hl,(0f579h)		;f0b0	2a 79 f5 	* y . 
	ld (lf687h),hl		;f0b3	22 87 f6 	" . . 
	ld hl,lf569h		;f0b6	21 69 f5 	! i . 
	ld (0f579h),hl		;f0b9	22 79 f5 	" y . 
	ld c,002h		;f0bc	0e 02 	. . 
	call sub_f034h		;f0be	cd 34 f0 	. 4 . 
	ret z			;f0c1	c8 	. 
	ld hl,(lf687h)		;f0c2	2a 87 f6 	* . . 
	ld (0f579h),hl		;f0c5	22 79 f5 	" y . 
	ld hl,lf686h		;f0c8	21 86 f6 	! . . 
	ld (hl),00dh		;f0cb	36 0d 	6 . 
lf0cdh:
	ld a,01fh		;f0cd	3e 1f 	> . 
	ld hl,lf686h		;f0cf	21 86 f6 	! . . 
	cp (hl)			;f0d2	be 	. 
	jr c,lf0e7h		;f0d3	38 12 	8 . 
	ld hl,(lf686h)		;f0d5	2a 86 f6 	* . . 
	ld h,000h		;f0d8	26 00 	& . 
	ex de,hl			;f0da	eb 	. 
	ld hl,(0f579h)		;f0db	2a 79 f5 	* y . 
	add hl,de			;f0de	19 	. 
	ld (hl),000h		;f0df	36 00 	6 . 
	ld hl,lf686h		;f0e1	21 86 f6 	! . . 
	inc (hl)			;f0e4	34 	4 
	jr nz,lf0cdh		;f0e5	20 e6 	  . 
lf0e7h:
	call sub_f03dh		;f0e7	cd 3d f0 	. = . 
	ld hl,PDOS_flags		;f0ea	21 24 f5 	! $ . 
	set 0,(hl)		;f0ed	cb c6 	. . 
	ret			;f0ef	c9 	. 
sub_f0f0h:
	ld hl,lf689h		;f0f0	21 89 f6 	! . . 
	ld (hl),c			;f0f3	71 	q 
	call sub_f09bh		;f0f4	cd 9b f0 	. . . 
	call sub_f037h		;f0f7	cd 37 f0 	. 7 . 
	ret z			;f0fa	c8 	. 
	ld bc,0000ch		;f0fb	01 0c 00 	. . . 
	ld hl,(0f579h)		;f0fe	2a 79 f5 	* y . 
	add hl,bc			;f101	09 	. 
	inc (hl)			;f102	34 	4 
	call sub_f032h		;f103	cd 32 f0 	. 2 . 
	jr nz,lf112h		;f106	20 0a 	  . 
	ld a,(lf689h)		;f108	3a 89 f6 	: . . 
	rra			;f10b	1f 	. 
	ret c			;f10c	d8 	. 
	call sub_f0adh		;f10d	cd ad f0 	. . . 
	jr lf115h		;f110	18 03 	. . 
lf112h:
	call sub_f068h		;f112	cd 68 f0 	. h . 
lf115h:
	call sub_f037h		;f115	cd 37 f0 	. 7 . 
	jr z,lf16ah		;f118	28 50 	( P 
	call sub_ec39h		;f11a	cd 39 ec 	. 9 . 
	ld hl,lf582h		;f11d	21 82 f5 	! . . 
	ld (hl),000h		;f120	36 00 	6 . 
	ret			;f122	c9 	. 
sub_f123h:
	ld a,(lf58bh)		;f123	3a 8b f5 	: . . 
	rra			;f126	1f 	. 
	call c,sub_e51fh		;f127	dc 1f e5 	. . . 
	ret			;f12a	c9 	. 
sub_f12bh:
	ld a,(lf58bh)		;f12b	3a 8b f5 	: . . 
	rra			;f12e	1f 	. 
	ret nc			;f12f	d0 	. 
sub_f130h:
	ld hl,(lf587h)		;f130	2a 87 f5 	* . . 
	ld (lf52eh),hl		;f133	22 2e f5 	" . . 
	ret			;f136	c9 	. 
sub_f137h:
	call sub_ec39h		;f137	cd 39 ec 	. 9 . 
	ld a,(lf665h)		;f13a	3a 65 f6 	: e . 
	ld hl,0f664h		;f13d	21 64 f6 	! d . 
	cp (hl)			;f140	be 	. 
	jr c,lf15fh		;f141	38 1c 	8 . 
	ld hl,lf582h		;f143	21 82 f5 	! . . 
	ld (hl),001h		;f146	36 01 	6 . 
	ld a,(lf665h)		;f148	3a 65 f6 	: e . 
	cp 040h		;f14b	fe 40 	. @ 
	jr nz,lf154h		;f14d	20 05 	  . 
	ld c,001h		;f14f	0e 01 	. . 
	call sub_f0f0h		;f151	cd f0 f0 	. . . 
lf154h:
	ld hl,lf665h		;f154	21 65 f6 	! e . 
	ld (hl),000h		;f157	36 00 	6 . 
	ld a,(lf582h)		;f159	3a 82 f5 	: . . 
	cp 000h		;f15c	fe 00 	. . 
	ret nz			;f15e	c0 	. 
lf15fh:
	call sub_ebffh		;f15f	cd ff eb 	. . . 
	ld hl,(lf666h)		;f162	2a 66 f6 	* f . 
	ld a,l			;f165	7d 	} 
	cp 000h		;f166	fe 00 	. . 
	jr nz,lf170h		;f168	20 06 	  . 
lf16ah:
	ld hl,lf582h		;f16a	21 82 f5 	! . . 
	ld (hl),001h		;f16d	36 01 	6 . 
	ret			;f16f	c9 	. 
lf170h:
	call sub_ec19h		;f170	cd 19 ec 	. . . 
	call leb9eh		;f173	cd 9e eb 	. . . 
	call sub_f123h		;f176	cd 23 f1 	. # . 
	call Seek_to_track		;f179	cd 2c e7 	. , . 
	call sub_e8b3h		;f17c	cd b3 e8 	. . . 
	call sub_f12bh		;f17f	cd 2b f1 	. + . 
	call sub_ec50h		;f182	cd 50 ec 	. P . 
	ret			;f185	c9 	. 
sub_f186h:
	call sub_ece0h		;f186	cd e0 ec 	. . . 
	call sub_ec39h		;f189	cd 39 ec 	. 9 . 
	ld a,03fh		;f18c	3e 3f 	> ? 
	ld hl,lf665h		;f18e	21 65 f6 	! e . 
	cp (hl)			;f191	be 	. 
	jr c,lf16ah		;f192	38 d6 	8 . 
	call sub_ebffh		;f194	cd ff eb 	. . . 
	ld hl,(lf666h)		;f197	2a 66 f6 	* f . 
	ld a,l			;f19a	7d 	} 
	cp 000h		;f19b	fe 00 	. . 
	jp nz,lf1f9h		;f19d	c2 f9 f1 	. . . 
	ld hl,lf68bh		;f1a0	21 8b f6 	! . . 
	ld (hl),000h		;f1a3	36 00 	6 . 
	ld a,(lf665h)		;f1a5	3a 65 f6 	: e . 
	and 0fch		;f1a8	e6 fc 	. . 
	rra			;f1aa	1f 	. 
	rra			;f1ab	1f 	. 
	add a,010h		;f1ac	c6 10 	. . 
	inc hl			;f1ae	23 	# 
	ld (hl),a			;f1af	77 	w 
	ld c,a			;f1b0	4f 	O 
	ld a,010h		;f1b1	3e 10 	> . 
	cp c			;f1b3	b9 	. 
	jr nc,lf1c5h		;f1b4	30 0f 	0 . 
	ld a,(lf68ch)		;f1b6	3a 8c f6 	: . . 
	dec a			;f1b9	3d 	= 
	ld c,a			;f1ba	4f 	O 
	ld b,000h		;f1bb	06 00 	. . 
	ld hl,(0f579h)		;f1bd	2a 79 f5 	* y . 
	add hl,bc			;f1c0	09 	. 
	ld a,(hl)			;f1c1	7e 	~ 
	ld (lf68bh),a		;f1c2	32 8b f6 	2 . . 
lf1c5h:
	ld hl,(lf68bh)		;f1c5	2a 8b f6 	* . . 
	ld c,l			;f1c8	4d 	M 
	call sub_ef7dh		;f1c9	cd 7d ef 	. } . 
	ld (lf68bh),a		;f1cc	32 8b f6 	2 . . 
	cp 000h		;f1cf	fe 00 	. . 
	jr nz,lf1dah		;f1d1	20 07 	  . 
	ld hl,lf582h		;f1d3	21 82 f5 	! . . 
	ld (hl),002h		;f1d6	36 02 	6 . 
	jr lf1f9h		;f1d8	18 1f 	. . 
lf1dah:
	ld hl,(lf68bh)		;f1da	2a 8b f6 	* . . 
	ld c,l			;f1dd	4d 	M 
	ld e,001h		;f1de	1e 01 	. . 
	call sub_edb3h		;f1e0	cd b3 ed 	. . . 
	ld hl,(lf68bh)		;f1e3	2a 8b f6 	* . . 
	ld h,000h		;f1e6	26 00 	& . 
	ld (lf666h),hl		;f1e8	22 66 f6 	" f . 
	ld hl,(lf68ch)		;f1eb	2a 8c f6 	* . . 
	ld h,000h		;f1ee	26 00 	& . 
	ex de,hl			;f1f0	eb 	. 
	ld hl,(0f579h)		;f1f1	2a 79 f5 	* y . 
	add hl,de			;f1f4	19 	. 
	ld a,(lf68bh)		;f1f5	3a 8b f6 	: . . 
	ld (hl),a			;f1f8	77 	w 
lf1f9h:
	ld a,(lf582h)		;f1f9	3a 82 f5 	: . . 
	cp 000h		;f1fc	fe 00 	. . 
	ret nz			;f1fe	c0 	. 
	call sub_ec19h		;f1ff	cd 19 ec 	. . . 
	call leb9eh		;f202	cd 9e eb 	. . . 
	call sub_f123h		;f205	cd 23 f1 	. # . 
	call Seek_to_track		;f208	cd 2c e7 	. , . 
	call led32h		;f20b	cd 32 ed 	. 2 . 
	call sub_f12bh		;f20e	cd 2b f1 	. + . 
	ld a,(lf665h)		;f211	3a 65 f6 	: e . 
	ld hl,0f664h		;f214	21 64 f6 	! d . 
	cp (hl)			;f217	be 	. 
	jr c,lf221h		;f218	38 07 	8 . 
	ld a,(lf665h)		;f21a	3a 65 f6 	: e . 
	inc a			;f21d	3c 	< 
	ld (0f664h),a		;f21e	32 64 f6 	2 d . 
lf221h:
	ld a,(lf665h)		;f221	3a 65 f6 	: e . 
	cp 03fh		;f224	fe 3f 	. ? 
	jr nz,lf248h		;f226	20 20 	    
	ld hl,060d0h		;f228	21 d0 60 	! . ` 
	bit 1,(hl)		;f22b	cb 4e 	. N 
	jr nz,lf248h		;f22d	20 19 	  . 
	call sub_ec50h		;f22f	cd 50 ec 	. P . 
	ld c,000h		;f232	0e 00 	. . 
	call sub_f0f0h		;f234	cd f0 f0 	. . . 
	ld a,(lf582h)		;f237	3a 82 f5 	: . . 
	cp 000h		;f23a	fe 00 	. . 
	jr nz,lf243h		;f23c	20 05 	  . 
	ld hl,lf665h		;f23e	21 65 f6 	! e . 
	ld (hl),0ffh		;f241	36 ff 	6 . 
lf243h:
	ld hl,lf582h		;f243	21 82 f5 	! . . 
	ld (hl),000h		;f246	36 00 	6 . 
lf248h:
	call sub_ec50h		;f248	cd 50 ec 	. P . 
	ret			;f24b	c9 	. 


sub_f24ch:
	ld a,(Active_drive)		;f24c	3a 76 f5 	: v . 
	inc a			;f24f	3c 	< 
	ld (Disk_flag),a		;f250	32 0f 69 	2 . i 
	dec a			;f253	3d 	= 
	cp 004h		;f254	fe 04 	. . 
	jp nc,leb30h		;f256	d2 30 eb 	. 0 . 
	inc a			;f259	3c 	< 
	call sub_f27bh		;f25a	cd 7b f2 	. { . 
	ld a,(lf653h)		;f25d	3a 53 f6 	: S . 
	rlca			;f260	07 	. 
	push af			;f261	f5 	. 
	ld a,(Active_drive)		;f262	3a 76 f5 	: v . 
	inc a			;f265	3c 	< 
	ld c,a			;f266	4f 	O 
	pop af			;f267	f1 	. 
	call sub_f430h		;f268	cd 30 f4 	. 0 . 
	rra			;f26b	1f 	. 
	ret c			;f26c	d8 	. 
	ld hl,(lf653h)		;f26d	2a 53 f6 	* S . 
	ld c,l			;f270	4d 	M 
	call sub_ecabh		;f271	cd ab ec 	. . . 
	ld (lf653h),a		;f274	32 53 f6 	2 S . 
	call sub_ee25h		;f277	cd 25 ee 	. % . 
	ret			;f27a	c9 	. 

sub_f27bh:
	call set_active_drive				;f27b	cd 5f eb 	. _ . 
	ld b,a								; drive # in B
	ld hl,0x0000						; offset = 0
	ld de,0x0020						; 20 bytes per drive

lf285h:
	djnz lf290h							; Skip 32 (0x20) bytes per drive

; drive indez in hl
	ld de,lf58ch						; ffff = -1 ??
	add hl,de							; point to ??
	ld (0f60ch),hl						; save pointer do drive info?

	jr lf293h		;f28e	18 03 	. . 

lf290h:
	add hl,de			;f290	19 	. 
	jr lf285h		;f291	18 f2 	. . 

lf293h:
	ld b,a			;f293	47 	G 
	ld hl,0x0000		;f294	21 00 00 	! . . 
	ld de,0x0010		;f297	11 10 00 	. . . 
lf29ah:
	djnz lf2a5h		;f29a	10 09 	. . 
	ld de,lf60eh+1		;f29c	11 0f f6 	. . . 
	add hl,de			;f29f	19 	. 
	ld (lf64fh),hl		;f2a0	22 4f f6 	" O . 
	jr lf2a8h		;f2a3	18 03 	. . 
lf2a5h:
	add hl,de			;f2a5	19 	. 
	jr lf29ah		;f2a6	18 f2 	. . 
lf2a8h:
	ld hl,(Active_drive)		;f2a8	2a 76 f5 	* v . 
	ld h,000h		;f2ab	26 00 	& . 
	ld bc,lf653h+1		;f2ad	01 54 f6 	. T . 
	add hl,bc			;f2b0	09 	. 
	ld (lf660h),hl		;f2b1	22 60 f6 	" ` . 
	ld hl,(Active_drive)		;f2b4	2a 76 f5 	* v . 
	ld h,000h		;f2b7	26 00 	& . 
	ld bc,lf658h		;f2b9	01 58 f6 	. X . 
	add hl,hl			;f2bc	29 	) 
	add hl,bc			;f2bd	09 	. 
	ld (0f662h),hl		;f2be	22 62 f6 	" b . 
	ret			;f2c1	c9 	. 
sub_f2c2h:
	ld hl,Active_drive		;f2c2	21 76 f5 	! v . 
	ld a,(lf583h)		;f2c5	3a 83 f5 	: . . 
	cp (hl)			;f2c8	be 	. 
	ret z			;f2c9	c8 	. 
	ld a,(lf583h)		;f2ca	3a 83 f5 	: . . 
	ld (Active_drive),a		;f2cd	32 76 f5 	2 v . 
	call sub_f24ch		;f2d0	cd 4c f2 	. L . 
	ret			;f2d3	c9 	. 
sub_f2d4h:
	ld hl,(0f579h)		;f2d4	2a 79 f5 	* y . 
	ld a,01fh		;f2d7	3e 1f 	> . 
	and (hl)			;f2d9	a6 	. 
	dec a			;f2da	3d 	= 
	ld (lf583h),a		;f2db	32 83 f5 	2 . . 
	cp 01eh		;f2de	fe 1e 	. . 
	ret nc			;f2e0	d0 	. 
	ld a,(Active_drive)		;f2e1	3a 76 f5 	: v . 
	ld (last_used_drive),a		;f2e4	32 51 f6 	2 Q . 
	ld a,(hl)			;f2e7	7e 	~ 
	ld (lf652h),a		;f2e8	32 52 f6 	2 R . 
	ld a,0e0h		;f2eb	3e e0 	> . 
	and (hl)			;f2ed	a6 	. 
	ld (hl),a			;f2ee	77 	w 
	call sub_f2c2h		;f2ef	cd c2 f2 	. . . 
	ret			;f2f2	c9 	. 
lf2f3h:
	ld hl,(0f579h)		;f2f3	2a 79 f5 	* y . 
	ld (lf587h),hl		;f2f6	22 87 f5 	" . . 
	ld (lf52eh),hl		;f2f9	22 2e f5 	" . . 
	ret			;f2fc	c9 	. 
sub_f2fdh:
	ld hl,(0f579h)		;f2fd	2a 79 f5 	* y . 
	ld a,l			;f300	7d 	} 
	ld (lf583h),a		;f301	32 83 f5 	2 . . 
	ld hl,0x0000		;f304	21 00 00 	! . . 
lf307h:
	ld (lf57bh),hl		;f307	22 7b f5 	" { . 
	ld a,l			;f30a	7d 	} 
	ld (lf582h),a		;f30b	32 82 f5 	2 . . 
	ld (lf652h),a		;f30e	32 52 f6 	2 R . 
	ld hl,(0f578h)		;f311	2a 78 f5 	* x . 
	ld c,l			;f314	4d 	M 
	ld b,000h		;f315	06 00 	. . 
	ld hl,lf307h		;f317	21 07 f3 	! . . 
	add hl,bc			;f31a	09 	. 
	add hl,bc			;f31b	09 	. 
	ld e,(hl)			;f31c	5e 	^ 
	inc hl			;f31d	23 	# 
	ld d,(hl)			;f31e	56 	V 
	ex de,hl			;f31f	eb 	. 
	jp (hl)			;f320	e9 	. 
	ld b,l			;f321	45 	E 
	di			;f322	f3 	. 
	ld h,l			;f323	65 	e 
	di			;f324	f3 	. 
	ld (hl),b			;f325	70 	p 
	di			;f326	f3 	. 
	ld a,b			;f327	78 	x 
	di			;f328	f3 	. 
	add a,b			;f329	80 	. 
	di			;f32a	f3 	. 
	adc a,d			;f32b	8a 	. 
	di			;f32c	f3 	. 
	sbc a,b			;f32d	98 	. 
	di			;f32e	f3 	. 
	and b			;f32f	a0 	. 
	di			;f330	f3 	. 
	xor b			;f331	a8 	. 
	di			;f332	f3 	. 
	or b			;f333	b0 	. 
	di			;f334	f3 	. 
	cp b			;f335	b8 	. 
	di			;f336	f3 	. 
	ret nz			;f337	c0 	. 
	di			;f338	f3 	. 
	push bc			;f339	c5 	. 
	di			;f33a	f3 	. 
	jp z,0ddf3h		;f33b	ca f3 dd 	. . . 
	di			;f33e	f3 	. 
	push hl			;f33f	e5 	. 
	di			;f340	f3 	. 
	jp pe,lf2f3h		;f341	ea f3 f2 	. . . 
	di			;f344	f3 	. 
	xor a			;f345	af 	. 
	ld (lf653h),a		;f346	32 53 f6 	2 S . 
	call sub_e56bh		;f349	cd 6b e5 	. k . 
	jr nz,lf35ah		;f34c	20 0c 	  . 
	ld (Active_drive),a		;f34e	32 76 f5 	2 v . 
	ld (last_used_drive),a		;f351	32 51 f6 	2 Q . 
	ld hl,06280h		;f354	21 80 62 	! . b 
	ld (0f579h),hl		;f357	22 79 f5 	" y . 
lf35ah:
	call sub_e563h		;f35a	cd 63 e5 	. c . 
	call lf2f3h		;f35d	cd f3 f2 	. . . 
	call sub_f24ch		;f360	cd 4c f2 	. L . 
	jr lf388h		;f363	18 23 	. # 
	call sub_f2c2h		;f365	cd c2 f2 	. . . 
	ld a,(Active_drive)		;f368	3a 76 f5 	: v . 
	ld (last_used_drive),a		;f36b	32 51 f6 	2 Q . 
	jr lf388h		;f36e	18 18 	. . 
	call sub_f2d4h		;f370	cd d4 f2 	. . . 
	call sub_f068h		;f373	cd 68 f0 	. h . 
	jr lf388h		;f376	18 10 	. . 
	call sub_f2d4h		;f378	cd d4 f2 	. . . 
	call sub_f09bh		;f37b	cd 9b f0 	. . . 
	jr lf388h		;f37e	18 08 	. . 
	call sub_f2d4h		;f380	cd d4 f2 	. . . 
	ld c,00dh		;f383	0e 0d 	. . 
	call sub_ef3dh		;f385	cd 3d ef 	. = . 
lf388h:
	jr lf3fah		;f388	18 70 	. p 
	ld hl,(lf679h)		;f38a	2a 79 f6 	* y . 
	ld (0f579h),hl		;f38d	22 79 f5 	" y . 
	call sub_f2d4h		;f390	cd d4 f2 	. . . 
	call sub_eeadh		;f393	cd ad ee 	. . . 
	jr lf3fah		;f396	18 62 	. b 
	call sub_f2d4h		;f398	cd d4 f2 	. . . 
	call sub_ef57h		;f39b	cd 57 ef 	. W . 
	jr lf3fah		;f39e	18 5a 	. Z 
	call sub_f2d4h		;f3a0	cd d4 f2 	. . . 
	call sub_f137h		;f3a3	cd 37 f1 	. 7 . 
	jr lf3fah		;f3a6	18 52 	. R 
	call sub_f2d4h		;f3a8	cd d4 f2 	. . . 
	call sub_f186h		;f3ab	cd 86 f1 	. . . 
	jr lf3fah		;f3ae	18 4a 	. J 
	call sub_f2d4h		;f3b0	cd d4 f2 	. . . 
	call sub_f0adh		;f3b3	cd ad f0 	. . . 
	jr lf3fah		;f3b6	18 42 	. B 
	call sub_f2d4h		;f3b8	cd d4 f2 	. . . 
	call sub_f045h		;f3bb	cd 45 f0 	. E . 
	jr lf3fah		;f3be	18 3a 	. : 
	ld a,(lf653h)		;f3c0	3a 53 f6 	: S . 
	jr lf3edh		;f3c3	18 28 	. ( 
	ld a,(Active_drive)		;f3c5	3a 76 f5 	: v . 
	jr lf3edh		;f3c8	18 23 	. # 
	ld a,(lf58bh)		;f3ca	3a 8b f5 	: . . 
	rra			;f3cd	1f 	. 
	jr nc,lf3d8h		;f3ce	30 08 	0 . 
	ld hl,(0f579h)		;f3d0	2a 79 f5 	* y . 
	ld (lf589h),hl		;f3d3	22 89 f5 	" . . 
	jr lf3dbh		;f3d6	18 03 	. . 
lf3d8h:
	call lf2f3h		;f3d8	cd f3 f2 	. . . 
lf3dbh:
	jr lf3fah		;f3db	18 1d 	. . 
	ld hl,(0f60ch)		;f3dd	2a 0c f6 	* . . 
	ld (lf57bh),hl		;f3e0	22 7b f5 	" { . 
	jr lf3fah		;f3e3	18 15 	. . 
	call sub_ecd5h		;f3e5	cd d5 ec 	. . . 
	jr lf3fah		;f3e8	18 10 	. . 
	ld a,(lf60eh)		;f3ea	3a 0e f6 	: . . 
lf3edh:
	ld (lf582h),a		;f3ed	32 82 f5 	2 . . 
	jr lf3fah		;f3f0	18 08 	. . 
	ld hl,lf58bh		;f3f2	21 8b f5 	! . . 
	ld (hl),001h		;f3f5	36 01 	6 . 
	call lf2f3h		;f3f7	cd f3 f2 	. . . 
lf3fah:
	ld a,(lf652h)		;f3fa	3a 52 f6 	: R . 
	cp 000h		;f3fd	fe 00 	. . 
	jr z,lf411h		;f3ff	28 10 	( . 
	ld hl,(0f579h)		;f401	2a 79 f5 	* y . 
	ld a,(lf652h)		;f404	3a 52 f6 	: R . 
	ld (hl),a			;f407	77 	w 
	ld a,(last_used_drive)		;f408	3a 51 f6 	: Q . 
	ld (lf583h),a		;f40b	32 83 f5 	2 . . 
	call sub_f2c2h		;f40e	cd c2 f2 	. . . 
lf411h:
	ld a,(lf582h)		;f411	3a 82 f5 	: . . 
	ld de,lf57bh		;f414	11 7b f5 	. { . 
	ex de,hl			;f417	eb 	. 
	ld e,a			;f418	5f 	_ 
	ld d,000h		;f419	16 00 	. . 
	ex de,hl			;f41b	eb 	. 
	ld a,(de)			;f41c	1a 	. 
	or l			;f41d	b5 	. 
	ld l,a			;f41e	6f 	o 
	inc de			;f41f	13 	. 
	ld a,(de)			;f420	1a 	. 
	or h			;f421	b4 	. 
	ld h,a			;f422	67 	g 
	ex de,hl			;f423	eb 	. 
	dec hl			;f424	2b 	+ 
	ld (hl),e			;f425	73 	s 
	inc hl			;f426	23 	# 
	ld (hl),d			;f427	72 	r 
	ret			;f428	c9 	. 
sub_f429h:
	ld a,(hl)			;f429	7e 	~ 
sub_f42ah:
	rlca			;f42a	07 	. 
	dec c			;f42b	0d 	. 
	jr nz,sub_f42ah		;f42c	20 fc 	  . 
	ret			;f42e	c9 	. 
sub_f42fh:
	ld a,(hl)			;f42f	7e 	~ 
sub_f430h:
	rrca			;f430	0f 	. 
	dec c			;f431	0d 	. 
	jr nz,sub_f430h		;f432	20 fc 	  . 
	ret			;f434	c9 	. 
sub_f435h:
	ld e,(hl)			;f435	5e 	^ 
	inc hl			;f436	23 	# 
	ld d,(hl)			;f437	56 	V 
	ex de,hl			;f438	eb 	. 
lf439h:
	add hl,hl			;f439	29 	) 
	dec c			;f43a	0d 	. 
	jr nz,lf439h		;f43b	20 fc 	  . 
	ret			;f43d	c9 	. 
sub_f43eh:
	ld a,(hl)			;f43e	7e 	~ 
lf43fh:
	or a			;f43f	b7 	. 
	rra			;f440	1f 	. 
	dec c			;f441	0d 	. 
	jr nz,lf43fh		;f442	20 fb 	  . 
	ret			;f444	c9 	. 
	ld l,c			;f445	69 	i 
	ld h,b			;f446	60 	` 
sub_f447h:
	ld hl,(0f662h)		;f447	2a 62 f6 	* b . 
	ld de,lf666h		;f44a	11 66 f6 	. f . 
	ld c,(hl)			;f44d	4e 	N 
	inc hl			;f44e	23 	# 
	ld b,(hl)			;f44f	46 	F 
	ld a,(de)			;f450	1a 	. 
	sub c			;f451	91 	. 
	ld l,a			;f452	6f 	o 
	inc de			;f453	13 	. 
	ld a,(de)			;f454	1a 	. 
	sbc a,b			;f455	98 	. 
	ld h,a			;f456	67 	g 
	ret			;f457	c9 	. 
sub_f458h:
	ld l,a			;f458	6f 	o 
	ld h,000h		;f459	26 00 	& . 
sub_f45bh:
	ld a,(de)			;f45b	1a 	. 
	sub l			;f45c	95 	. 
	ld l,a			;f45d	6f 	o 
	inc de			;f45e	13 	. 
	ld a,(de)			;f45f	1a 	. 
	sbc a,h			;f460	9c 	. 
	ld h,a			;f461	67 	g 
	ret			;f462	c9 	. 

stringtable_start:
	db				0x0e
	db				'DIRECTORY FULL'						; 1
	db				0x09
	db				'DISK FULL'								; 2

	db				0x01, 0x20, 0x01, 0x20, 0x01, 0x20		; 3, 4, 5

	db				0x0e
	db				'DISK NOT READY'						; 6

	db				0x14
	db				'DISK WRITE PROTECTED'					; 7
	db				0x12
	db				'WRONG DRIVE NUMBER'					; 8
	db				0x0b
	db				'SYSTEM DISK'							; 9
	db				0x20
	db				' DIRECTORY     FREE SPACE IN K :'		; 10
	db				0x21
	db				'TURN PAGE      *...WRITEPROTECTED'		; 11
	db				0x13
	db				'DISKS ARE NOT EQUAL'					; 12

Retry_counter:
	ld c,00dh		;f51c	0e 0d 	. . 
Disk_error_code:
	ld (bc),a			;f51e	02 	. 
	rst 38h			;f51f	ff 	. 
lf520h:
	ld bc,00201h		;f520	01 01 02 	. . . 
	db			0x01
PDOS_flags:
	db			0x40 

Disk_status_buffer:
	db 			0xc9
	nop			;f526	00 	. 
	nop			;f527	00 	. 
	nop			;f528	00 	. 
	nop			;f529	00 	. 
	db			0x10
	db			0x01

lf52ch:
	ld l,c			;f52c	69 	i 
lf52dh:
	rst 38h			;f52d	ff 	. 
lf52eh:
	add a,b			;f52e	80 	. 
lf52fh:
	ld h,d			;f52f	62 	b 
lf530h:
	ld (hl),d			;f530	72 	r 
	ld h,b			;f531	60 	` 
ix_pointer:
	dw				0xe000
stack_store:
	dw				0x612e 


DISK_RW_Command:
	db				0x09			; command length
RW_cmd_action:
	db				01000110b		; command = Read (modified by code to write is necessary)
RW_cmd_hd_drive_select:
	db				0x01			; drive & head select
RW_cmd_track:	
	db				0x01			; Cylinder
RW_cmd_head:
	db				0x00			; Head
RW_cmd_sector:
	db				0x10			; Record
	db				0x01			; data bytes
	db				0x10			; EOT
	db				0x0e			; GPL
	db				0x00			; DTL

DISK_SEEK_Command:
	db				0x03			; command length
	db				0x0f			; SEEK command = 00001111b
SEEK_cmd_hd_drive_select:
	db				0x01			; drive & head select
SEEK_cmd_track:
	db				0x01

DISK_RECALIBRATE_Command:
	db				0x02
	db				0x07
RECALIBRATE_cmd_hd_drive_select:
	db				0x01			; f546


	db				0x04			; f547
	db				0x00			; f548
	db				0x00			; f549	00 	. 
	db				0x00			; f54a	00 	. 
	db				0x01			; f54b

DISK_SPECIFY_COMMAND:
	db			0x03			; 3 bytes
	db			0x03			; SPECIFY = 00000011b 
	db			0x60			; SRT : 6 = 6ms,   HUT = 0 16ms 
	db			0x34			; HLT 34 = 52d = 104 ms 

lf550h:
	nop			;f550	00 	. 
Drive_head_table:
	nop			;f551	00 	. 
	nop			;f552	00 	. 
	nop			;f553	00 	. 
	nop			;f554	00 	. 
lf555h:
	ld bc,00d07h		;f555	01 07 0d 	. . . 
	inc bc			;f558	03 	. 
	add hl,bc			;f559	09 	. 
	rrca			;f55a	0f 	. 
	dec b			;f55b	05 	. 
	dec bc			;f55c	0b 	. 
lf55dh:
	ld (bc),a			;f55d	02 	. 
	ex af,af'			;f55e	08 	. 
	ld c,004h		;f55f	0e 04 	. . 
	ld a,(bc)			;f561	0a 	. 
lf562h:
	djnz lf56ah		;f562	10 06 	. . 
	inc c			;f564	0c 	. 
	nop			;f565	00 	. 
	nop			;f566	00 	. 
	nop			;f567	00 	. 
	nop			;f568	00 	. 
lf569h:
	nop			;f569	00 	. 
lf56ah:
	nop			;f56a	00 	. 
lf56bh:
	db				0x00
	db				0x01	;-6c
lf56dh:
	db				0x10	;-6d	
	db				0x80	;-6e	
;	nop			;f56b	00 	. 
;	ld bc,08010h		;f56c	01 10 80 	. . . 
lf56fh:
	ld (bc),a			;f56f	02 	. 
lf570h:
	inc bc			;f570	03 	. 
lf571h:
	inc hl			;f571	23 	# 
lf572h:
	adc a,b			;f572	88 	. 
lf573h:
	adc a,e			;f573	8b 	. 
lf574h:
	ret p			;f574	f0 	. 
lf575h:
	nop			;f575	00 	. 
Active_drive:
	nop			;f576	00 	. 
	ld bc,0800dh		;f577	01 0d 80 	. . . 
	ld h,d			;f57a	62 	b 
lf57bh:
	dw				0

lf57dh:
	rst 38h			;f57d	ff 	. 
	rst 38h			;f57e	ff 	. 
lf57fh:
	rst 38h			;f57f	ff 	. 
lf580h:
	rst 38h			;f580	ff 	. 
lf581h:
	rst 38h			;f581	ff 	. 
lf582h:
	nop			;f582	00 	. 
lf583h:
	ld l,c			;f583	69 	i 
lf584h:
	nop			;f584	00 	. 
lf585h:
	ld l,b			;f585	68 	h 
lf586h:
	dec c			;f586	0d 	. 
lf587h:
	add a,b			;f587	80 	. 
	ld h,d			;f588	62 	b 
lf589h:
	add a,b			;f589	80 	. 
	ld h,d			;f58a	62 	b 
lf58bh:
	db				0x01
lf58ch:
	dw				0xffff
;	ld bc,lfffeh+1		;f58b	01 ff ff 	. . . 
	rst 38h			;f58e	ff 	. 
	rst 18h			;f58f	df 	. 
	cp 0fbh		;f590	fe fb 	. . 
	rst 38h			;f592	ff 	. 
	cp 0ffh		;f593	fe ff 	. . 
	rst 8			;f595	cf 	. 
lf596h:
	pop af			;f596	f1 	. 
	di			;f597	f3 	. 
	rst 10h			;f598	d7 	. 
	ld e,a			;f599	5f 	_ 
	rst 38h			;f59a	ff 	. 
	rst 38h			;f59b	ff 	. 
	rst 28h			;f59c	ef 	. 
	ret nz			;f59d	c0 	. 
	nop			;f59e	00 	. 
	nop			;f59f	00 	. 
	nop			;f5a0	00 	. 
	nop			;f5a1	00 	. 
	nop			;f5a2	00 	. 
	nop			;f5a3	00 	. 
	nop			;f5a4	00 	. 
	nop			;f5a5	00 	. 
lf5a6h:
	djnz lf596h		;f5a6	10 ee 	. . 
	ld d,e			;f5a8	53 	S 
	xor 010h		;f5a9	ee 10 	. . 
	call pe,sub_ec11h		;f5ab	ec 11 ec 	. . . 
	ld d,b			;f5ae	50 	P 
	xor 011h		;f5af	ee 11 	. . 
	xor 010h		;f5b1	ee 10 	. . 
	call pe,sub_ec50h+1		;f5b3	ec 51 ec 	. Q . 
	djnz lf5a6h		;f5b6	10 ee 	. . 
	ld d,c			;f5b8	51 	Q 
	xor 010h		;f5b9	ee 10 	. . 
	xor 011h		;f5bb	ee 11 	. . 
	xor 050h		;f5bd	ee 50 	. P 
	call z,0cc11h		;f5bf	cc 11 cc 	. . . 
	djnz $-16		;f5c2	10 ee 	. . 
	ld d,c			;f5c4	51 	Q 
	xor 010h		;f5c5	ee 10 	. . 
	xor 053h		;f5c7	ee 53 	. S 
	xor 010h		;f5c9	ee 10 	. . 
	xor 051h		;f5cb	ee 51 	. Q 
	xor 050h		;f5cd	ee 50 	. P 
	xor 013h		;f5cf	ee 13 	. . 
	xor 011h		;f5d1	ee 11 	. . 
	xor 011h		;f5d3	ee 11 	. . 
lf5d5h:
	xor 051h		;f5d5	ee 51 	. Q 
	xor 051h		;f5d7	ee 51 	. Q 
	xor 050h		;f5d9	ee 50 	. P 
	xor 011h		;f5db	ee 11 	. . 
	xor 010h		;f5dd	ee 10 	. . 
	nop			;f5df	00 	. 
	ld d,c			;f5e0	51 	Q 
	nop			;f5e1	00 	. 
	djnz $-16		;f5e2	10 ee 	. . 
	ld d,b			;f5e4	50 	P 
	xor 010h		;f5e5	ee 10 	. . 
	xor 052h		;f5e7	ee 52 	. R 
	xor 010h		;f5e9	ee 10 	. . 
	xor 011h		;f5eb	ee 11 	. . 
	xor 010h		;f5ed	ee 10 	. . 
	xor 012h		;f5ef	ee 12 	. . 
	xor 011h		;f5f1	ee 11 	. . 
	xor 010h		;f5f3	ee 10 	. . 
	xor 010h		;f5f5	ee 10 	. . 
	xor 050h		;f5f7	ee 50 	. P 
	call pe,0ee50h		;f5f9	ec 50 ee 	. P . 
	ld (de),a			;f5fc	12 	. 
	xor 010h		;f5fd	ee 10 	. . 
	adc a,b			;f5ff	88 	. 
	inc de			;f600	13 	. 
	ex af,af'			;f601	08 	. 
	ld de,013ech		;f602	11 ec 13 	. . . 
	xor 010h		;f605	ee 10 	. . 
	xor 010h		;f607	ee 10 	. . 
	xor 011h		;f609	ee 11 	. . 
	call pe,lf58ch		;f60b	ec 8c f5 	. . . 
lf60eh:
	ld bc,04b39h		;f60e	01 39 4b 	. 9 K 
	daa			;f611	27 	' 
	and d			;f612	a2 	. 
	or b			;f613	b0 	. 
	rst 20h			;f614	e7 	. 
	ld sp,hl			;f615	f9 	. 
	ld e,(hl)			;f616	5e 	^ 
	cp a			;f617	bf 	. 
	ld (hl),b			;f618	70 	p 
	ld a,l			;f619	7d 	} 
	ld a,a			;f61a	7f 	 
	xor b			;f61b	a8 	. 
	rst 38h			;f61c	ff 	. 
	rst 38h			;f61d	ff 	. 
	rst 38h			;f61e	ff 	. 
	rst 38h			;f61f	ff 	. 
	rst 38h			;f620	ff 	. 
	rst 38h			;f621	ff 	. 
	rst 38h			;f622	ff 	. 
	rst 38h			;f623	ff 	. 
	rst 38h			;f624	ff 	. 
	rst 38h			;f625	ff 	. 
	rst 38h			;f626	ff 	. 
	rst 38h			;f627	ff 	. 
	rst 38h			;f628	ff 	. 
	rst 38h			;f629	ff 	. 
	rst 38h			;f62a	ff 	. 
	rst 38h			;f62b	ff 	. 
	rst 38h			;f62c	ff 	. 
	rst 38h			;f62d	ff 	. 
	rst 38h			;f62e	ff 	. 
	rst 38h			;f62f	ff 	. 
	rst 38h			;f630	ff 	. 
	rst 38h			;f631	ff 	. 
	rst 38h			;f632	ff 	. 
	rst 38h			;f633	ff 	. 
	rst 38h			;f634	ff 	. 
	rst 38h			;f635	ff 	. 
	rst 38h			;f636	ff 	. 
	rst 38h			;f637	ff 	. 
	rst 38h			;f638	ff 	. 
	rst 38h			;f639	ff 	. 
	rst 38h			;f63a	ff 	. 
	rst 38h			;f63b	ff 	. 
	rst 38h			;f63c	ff 	. 
	rst 38h			;f63d	ff 	. 
	rst 38h			;f63e	ff 	. 
	rst 38h			;f63f	ff 	. 
	rst 38h			;f640	ff 	. 
	rst 38h			;f641	ff 	. 
	rst 38h			;f642	ff 	. 
	rst 38h			;f643	ff 	. 
	rst 38h			;f644	ff 	. 
	rst 38h			;f645	ff 	. 
	rst 38h			;f646	ff 	. 
	rst 38h			;f647	ff 	. 
	rst 38h			;f648	ff 	. 
	rst 38h			;f649	ff 	. 
	rst 38h			;f64a	ff 	. 
	rst 38h			;f64b	ff 	. 
	rst 38h			;f64c	ff 	. 
	rst 38h			;f64d	ff 	. 
	rst 38h			;f64e	ff 	. 
lf64fh:
	db				0x0f
	db				0xf6
last_used_drive:
	db				0x00
lf652h:
	nop			;f652	00 	. 
lf653h:
	ld bc,lff00h		;f653	01 00 ff 	. . . 
	rst 38h			;f656	ff 	. 
	rst 38h			;f657	ff 	. 
lf658h:
	nop			;f658	00 	. 
	nop			;f659	00 	. 
	rst 38h			;f65a	ff 	. 
	rst 38h			;f65b	ff 	. 
	rst 38h			;f65c	ff 	. 
	rst 38h			;f65d	ff 	. 
	rst 38h			;f65e	ff 	. 
	rst 38h			;f65f	ff 	. 
lf660h:
	ld d,h			;f660	54 	T 
	or 058h		;f661	f6 58 	. X 
	or 0ffh		;f663	f6 ff 	. . 
lf665h:
	rst 38h			;f665	ff 	. 
lf666h:
	dec c			;f666	0d 	. 
	nop			;f667	00 	. 
lf668h:
	djnz lf66ah		;f668	10 00 	. . 
lf66ah:
	rst 38h			;f66a	ff 	. 
	rst 38h			;f66b	ff 	. 
lf66ch:
	nop			;f66c	00 	. 
lf66dh:
	xor b			;f66d	a8 	. 
lf66eh:
	ld bc,kb_scan_row		;f66e	01 01 01 	. . . 
lf671h:
	ld bc,kb_scan_row		;f671	01 01 01 	. . . 
lf674h:
	ld bc,00100h		;f674	01 00 01 	. . . 
lf677h:
	ld a,(de)			;f677	1a 	. 
lf678h:
	rst 38h			;f678	ff 	. 
lf679h:
	rst 38h			;f679	ff 	. 
	rst 38h			;f67a	ff 	. 
lf67bh:
	rst 38h			;f67b	ff 	. 
lf67ch:
	rst 38h			;f67c	ff 	. 
lf67dh:
	rst 38h			;f67d	ff 	. 
	rst 38h			;f67e	ff 	. 
	rst 38h			;f67f	ff 	. 
	rst 38h			;f680	ff 	. 
lf681h:
	rst 38h			;f681	ff 	. 
lf682h:
	rst 38h			;f682	ff 	. 
lf683h:
	rst 38h			;f683	ff 	. 
lf684h:
	rst 38h			;f684	ff 	. 
lf685h:
	rst 38h			;f685	ff 	. 
lf686h:
	rst 38h			;f686	ff 	. 
lf687h:
	rst 38h			;f687	ff 	. 
	rst 38h			;f688	ff 	. 
lf689h:
	rst 38h			;f689	ff 	. 
	rst 38h			;f68a	ff 	. 
lf68bh:
	rst 38h			;f68b	ff 	. 
lf68ch:
	rst 38h			;f68c	ff 	. 
lf68dh:
	inc hl			;f68d	23 	# 
	adc a,b			;f68e	88 	. 
	adc a,e			;f68f	8b 	. 
lf690h:
	rst 38h			;f690	ff 	. 
	rst 38h			;f691	ff 	. 
	rst 38h			;f692	ff 	. 
lf693h:
	nop			;f693	00 	. 
lf694h:
	ld d,c			;f694	51 	Q 
	call pe,sub_ee52h		;f695	ec 52 ee 	. R . 
	ld de,050eeh		;f698	11 ee 50 	. . P 
	call pe,sub_ee52h		;f69b	ec 52 ee 	. R . 
	djnz lf6a0h		;f69e	10 00 	. . 
lf6a0h:
	ld d,c			;f6a0	51 	Q 
	nop			;f6a1	00 	. 
lf6a2h:
	djnz lf690h		;f6a2	10 ec 	. . 
lf6a4h:
	ld d,b			;f6a4	50 	P 
	xor 010h		;f6a5	ee 10 	. . 
	xor 053h		;f6a7	ee 53 	. S 
	xor 050h		;f6a9	ee 50 	. P 
	xor 050h		;f6ab	ee 50 	. P 
	xor 012h		;f6ad	ee 12 	. . 
	xor 051h		;f6af	ee 51 	. Q 
	xor 011h		;f6b1	ee 11 	. . 
lf6b3h:
	call pe,sub_ee53h		;f6b3	ec 53 ee 	. S . 
	ld (de),a			;f6b6	12 	. 
	xor 053h		;f6b7	ee 53 	. S 
	call pe,0ee50h		;f6b9	ec 50 ee 	. P . 
	ld d,e			;f6bc	53 	S 
	xor 010h		;f6bd	ee 10 	. . 
	call z,08c10h		;f6bf	cc 10 8c 	. . . 
	ld (de),a			;f6c2	12 	. 
	xor 053h		;f6c3	ee 53 	. S 
	xor 010h		;f6c5	ee 10 	. . 
	xor 052h		;f6c7	ee 52 	. R 
	xor 052h		;f6c9	ee 52 	. R 
	xor 053h		;f6cb	ee 53 	. S 
	xor 052h		;f6cd	ee 52 	. R 
	call pe,0ee51h		;f6cf	ec 51 ee 	. Q . 
	ld d,b			;f6d2	50 	P 
	xor 052h		;f6d3	ee 52 	. R 
dir_buffer:
	xor 010h		;f6d5	ee 10 	. . 
	xor 053h		;f6d7	ee 53 	. S 
	xor 050h		;f6d9	ee 50 	. P 
	xor 053h		;f6db	ee 53 	. S 
	xor 051h		;f6dd	ee 51 	. Q 
	nop			;f6df	00 	. 
	ld d,d			;f6e0	52 	R 
	nop			;f6e1	00 	. 
	ld (de),a			;f6e2	12 	. 
	xor 050h		;f6e3	ee 50 	. P 
lf6e5h:
	xor 010h		;f6e5	ee 10 	. . 
	xor 050h		;f6e7	ee 50 	. P 
	xor 010h		;f6e9	ee 10 	. . 
	xor 051h		;f6eb	ee 51 	. Q 
	xor 050h		;f6ed	ee 50 	. P 
	xor 051h		;f6ef	ee 51 	. Q 
	xor 052h		;f6f1	ee 52 	. R 
	call pe,sub_ee11h+1		;f6f3	ec 12 ee 	. . . 
lf6f6h:
	djnz $-16		;f6f6	10 ee 	. . 
lf6f8h:
	ld d,c			;f6f8	51 	Q 
	xor 011h		;f6f9	ee 11 	. . 
	xor 051h		;f6fb	ee 51 	. Q 
	xor 010h		;f6fd	ee 10 	. . 
	adc a,h			;f6ff	8c 	. 
	dec de			;f700	1b 	. 
	inc c			;f701	0c 	. 
	djnz $-16		;f702	10 ee 	. . 
	inc de			;f704	13 	. 
	xor 010h		;f705	ee 10 	. . 
	xor h			;f707	ac 	. 
	djnz lf6f6h		;f708	10 ec 	. . 
	djnz lf6f8h		;f70a	10 ec 	. . 
	ld de,011eeh		;f70c	11 ee 11 	. . . 
	xor h			;f70f	ac 	. 
	inc de			;f710	13 	. 
	call pe,0ee10h		;f711	ec 10 ee 	. . . 
	ld de,010ech		;f714	11 ec 10 	. . . 
	xor h			;f717	ac 	. 
	inc de			;f718	13 	. 
	call pe,0ee10h		;f719	ec 10 ee 	. . . 
	ld de,010aeh		;f71c	11 ae 10 	. . . 
	nop			;f71f	00 	. 
	ld de,Cartridge_ROM		;f720	11 00 10 	. . . 
	call pe,0ac10h		;f723	ec 10 ac 	. . . 
	ld de,013eeh		;f726	11 ee 13 	. . . 
	call pe,0ac10h		;f729	ec 10 ac 	. . . 
	ld de,010ech		;f72c	11 ec 10 	. . . 
	call pe,sub_ee11h		;f72f	ec 11 ee 	. . . 
	ld de,010ech		;f732	11 ec 10 	. . . 
	xor 010h		;f735	ee 10 	. . 
	call pe,sub_ee11h		;f737	ec 11 ee 	. . . 
	djnz $-18		;f73a	10 ec 	. . 
lf73ch:
	ld de,010eeh		;f73c	11 ee 10 	. . . 
	xor h			;f73f	ac 	. 
	djnz $-82		;f740	10 ac 	. . 
	djnz $-18		;f742	10 ec 	. . 
lf744h:
	ld de,010eeh		;f744	11 ee 10 	. . . 
	xor (hl)			;f747	ae 	. 
lf748h:
	ld de,010ech		;f748	11 ec 10 	. . . 
	call pe,sub_ee11h+1		;f74b	ec 12 ee 	. . . 
	djnz lf73ch		;f74e	10 ec 	. . 
	ld de,010ech		;f750	11 ec 10 	. . . 
	call pe,sub_ee11h		;f753	ec 11 ee 	. . . 
	djnz lf744h		;f756	10 ec 	. . 
	djnz $-18		;f758	10 ec 	. . 
	djnz lf748h		;f75a	10 ec 	. . 
	inc de			;f75c	13 	. 
	xor 010h		;f75d	ee 10 	. . 
	nop			;f75f	00 	. 
	djnz lf762h		;f760	10 00 	. . 
lf762h:
	djnz $-16		;f762	10 ee 	. . 
	ld de,010ech		;f764	11 ec 10 	. . . 
	xor 011h		;f767	ee 11 	. . 
	xor 010h		;f769	ee 10 	. . 
	call pe,0ae11h		;f76b	ec 11 ae 	. . . 
	ld de,011ech		;f76e	11 ec 11 	. . . 
	xor 010h		;f771	ee 10 	. . 
	call pe,0ee10h		;f773	ec 10 ee 	. . . 
	djnz $-82		;f776	10 ac 	. . 
	ld (de),a			;f778	12 	. 
	call pe,0ec10h		;f779	ec 10 ec 	. . . 
	djnz $-18		;f77c	10 ec 	. . 
	ld de,0538ch		;f77e	11 8c 53 	. . S 
	inc c			;f781	0c 	. 
	inc de			;f782	13 	. 
	xor 053h		;f783	ee 53 	. S 
	xor 013h		;f785	ee 13 	. . 
	xor 053h		;f787	ee 53 	. S 
	xor 013h		;f789	ee 13 	. . 
	xor 053h		;f78b	ee 53 	. S 
	xor 013h		;f78d	ee 13 	. . 
	xor 053h		;f78f	ee 53 	. S 
	xor 013h		;f791	ee 13 	. . 
	xor 053h		;f793	ee 53 	. S 
	xor 053h		;f795	ee 53 	. S 
	xor 053h		;f797	ee 53 	. S 
	xor 013h		;f799	ee 13 	. . 
	xor 053h		;f79b	ee 53 	. S 
	xor 053h		;f79d	ee 53 	. S 
	nop			;f79f	00 	. 
	ld d,e			;f7a0	53 	S 
	nop			;f7a1	00 	. 
	ld d,e			;f7a2	53 	S 
	xor 013h		;f7a3	ee 13 	. . 
	xor 053h		;f7a5	ee 53 	. S 
	xor 053h		;f7a7	ee 53 	. S 
	xor 013h		;f7a9	ee 13 	. . 
	xor 053h		;f7ab	ee 53 	. S 
	xor 013h		;f7ad	ee 13 	. . 
	xor 053h		;f7af	ee 53 	. S 
	xor 053h		;f7b1	ee 53 	. S 
	xor 053h		;f7b3	ee 53 	. S 
	xor 053h		;f7b5	ee 53 	. S 
	xor 053h		;f7b7	ee 53 	. S 
	xor 013h		;f7b9	ee 13 	. . 
	xor 013h		;f7bb	ee 13 	. . 
	xor 011h		;f7bd	ee 11 	. . 
	adc a,(hl)			;f7bf	8e 	. 
	ld d,e			;f7c0	53 	S 
	adc a,013h		;f7c1	ce 13 	. . 
	xor 053h		;f7c3	ee 53 	. S 
	xor 053h		;f7c5	ee 53 	. S 
	xor 053h		;f7c7	ee 53 	. S 
	xor 053h		;f7c9	ee 53 	. S 
	xor 013h		;f7cb	ee 13 	. . 
	xor 013h		;f7cd	ee 13 	. . 
	xor 053h		;f7cf	ee 53 	. S 
	xor 011h		;f7d1	ee 11 	. . 
	xor 053h		;f7d3	ee 53 	. S 
	rst 28h			;f7d5	ef 	. 
lf7d6h:
	inc de			;f7d6	13 	. 
	xor 053h		;f7d7	ee 53 	. S 
	xor 013h		;f7d9	ee 13 	. . 
	xor 053h		;f7db	ee 53 	. S 
	xor 053h		;f7dd	ee 53 	. S 
	nop			;f7df	00 	. 
	ld d,e			;f7e0	53 	S 
	nop			;f7e1	00 	. 
	inc de			;f7e2	13 	. 
	xor 013h		;f7e3	ee 13 	. . 
	rst 28h			;f7e5	ef 	. 
	djnz lf7d6h		;f7e6	10 ee 	. . 
	ld d,e			;f7e8	53 	S 
	rst 28h			;f7e9	ef 	. 
	inc de			;f7ea	13 	. 
	xor 053h		;f7eb	ee 53 	. S 
	xor 013h		;f7ed	ee 13 	. . 
	xor 013h		;f7ef	ee 13 	. . 
	xor 013h		;f7f1	ee 13 	. . 
	call pe,sub_ee53h		;f7f3	ec 53 ee 	. S . 
	ld d,e			;f7f6	53 	S 
	xor 053h		;f7f7	ee 53 	. S 
	xor 012h		;f7f9	ee 12 	. . 
	xor 053h		;f7fb	ee 53 	. S 
	xor 013h		;f7fd	ee 13 	. . 
	adc a,h			;f7ff	8c 	. 
	ld de,0120ch		;f800	11 0c 12 	. . . 
	xor 013h		;f803	ee 13 	. . 
	call pe,sub_ec12h		;f805	ec 12 ec 	. . . 
	ld (010ech),a		;f808	32 ec 10 	2 . . 
	call pe,sub_ec31h		;f80b	ec 31 ec 	. 1 . 
	djnz $-18		;f80e	10 ec 	. . 
	inc de			;f810	13 	. 
	call pe,sub_ee11h		;f811	ec 11 ee 	. . . 
	ld de,013ech		;f814	11 ec 13 	. . . 
	xor 013h		;f817	ee 13 	. . 
	call pe,0ec10h		;f819	ec 10 ec 	. . . 
lf81ch:
	ld de,010eeh		;f81c	11 ee 10 	. . . 
	nop			;f81f	00 	. 
	ld (hl),e			;f820	73 	s 
	nop			;f821	00 	. 
	ld de,013eeh		;f822	11 ee 13 	. . . 
	call pe,sub_ec73h		;f825	ec 73 ec 	. s . 
	ld de,013ech		;f828	11 ec 13 	. . . 
	call pe,sub_ee70h		;f82b	ec 70 ee 	. p . 
	djnz lf81ch		;f82e	10 ec 	. . 
lf830h:
	ld de,013ech		;f830	11 ec 13 	. . . 
	xor 071h		;f833	ee 71 	. q 
	xor 032h		;f835	ee 32 	. 2 
	call pe,0ec71h		;f837	ec 71 ec 	. q . 
	ld (de),a			;f83a	12 	. 
	xor 010h		;f83b	ee 10 	. . 
	xor 030h		;f83d	ee 30 	. 0 
	call pe,0ac50h		;f83f	ec 50 ac 	. P . 
	djnz lf830h		;f842	10 ec 	. . 
	ld sp,010eeh		;f844	31 ee 10 	1 . . 
	call pe,0ee31h		;f847	ec 31 ee 	. 1 . 
	jr nc,$-18		;f84a	30 ec 	0 . 
lf84ch:
	ld sp,050eeh		;f84c	31 ee 50 	1 . P 
	call pe,0ee10h		;f84f	ec 10 ee 	. . . 
	ld de,011ech		;f852	11 ec 11 	. . . 
	call pe,0ee30h		;f855	ec 30 ee 	. 0 . 
	djnz $-18		;f858	10 ec 	. . 
	ld (de),a			;f85a	12 	. 
	xor 070h		;f85b	ee 70 	. p 
	xor 010h		;f85d	ee 10 	. . 
	nop			;f85f	00 	. 
	ld de,Cartridge_ROM		;f860	11 00 10 	. . . 
	call pe,sub_ee11h		;f863	ec 11 ee 	. . . 
lf866h:
	djnz $-18		;f866	10 ec 	. . 
	inc de			;f868	13 	. 
	xor 033h		;f869	ee 33 	. 3 
	xor 053h		;f86b	ee 53 	. S 
	call pe,0ec10h		;f86d	ec 10 ec 	. . . 
	ld de,011ech		;f870	11 ec 11 	. . . 
	call pe,0ee51h		;f873	ec 51 ee 	. Q . 
	jr nc,lf866h		;f876	30 ee 	0 . 
	ld de,010eeh		;f878	11 ee 10 	. . . 
	xor 011h		;f87b	ee 11 	. . 
	xor 010h		;f87d	ee 10 	. . 
	adc a,h			;f87f	8c 	. 
	ld de,0110ch		;f880	11 0c 11 	. . . 
	call pe,sub_ec50h+1		;f883	ec 51 ec 	. Q . 
	djnz $-18		;f886	10 ec 	. . 
lf888h:
	ld de,010ech		;f888	11 ec 10 	. . . 
	call pe,sub_ec50h+1		;f88b	ec 51 ec 	. Q . 
	djnz $-18		;f88e	10 ec 	. . 
lf890h:
	ld de,052ech		;f890	11 ec 52 	. . R 
	call pe,0ec10h		;f893	ec 10 ec 	. . . 
	ld d,c			;f896	51 	Q 
	call pe,sub_ec50h+1		;f897	ec 51 ec 	. Q . 
	djnz lf888h		;f89a	10 ec 	. . 
	ld de,010ech		;f89c	11 ec 10 	. . . 
	ex af,af'			;f89f	08 	. 
	ld d,e			;f8a0	53 	S 
	nop			;f8a1	00 	. 
	djnz lf890h		;f8a2	10 ec 	. . 
lf8a4h:
	ld (de),a			;f8a4	12 	. 
	call pe,sub_ec11h		;f8a5	ec 11 ec 	. . . 
	ld d,c			;f8a8	51 	Q 
	call pe,sub_ec11h		;f8a9	ec 11 ec 	. . . 
	ld d,c			;f8ac	51 	Q 
	call pe,0ec10h		;f8ad	ec 10 ec 	. . . 
lf8b0h:
	ld de,011ech		;f8b0	11 ec 11 	. . . 
	call pe,sub_ec13h		;f8b3	ec 13 ec 	. . . 
lf8b6h:
	djnz lf8a4h		;f8b6	10 ec 	. . 
lf8b8h:
	ld de,010ech		;f8b8	11 ec 10 	. . . 
	call pe,sub_ee11h		;f8bb	ec 11 ee 	. . . 
	djnz lf84ch		;f8be	10 8c 	. . 
	djnz $-114		;f8c0	10 8c 	. . 
	djnz lf8b0h		;f8c2	10 ec 	. . 
lf8c4h:
	ld de,050ech		;f8c4	11 ec 50 	. . P 
	call pe,sub_ec11h		;f8c7	ec 11 ec 	. . . 
	djnz lf8b8h		;f8ca	10 ec 	. . 
	ld de,050ech		;f8cc	11 ec 50 	. . P 
	call pe,sub_ec11h		;f8cf	ec 11 ec 	. . . 
	ld (de),a			;f8d2	12 	. 
	call pe,sub_ec11h		;f8d3	ec 11 ec 	. . . 
	djnz lf8c4h		;f8d6	10 ec 	. . 
	ld d,b			;f8d8	50 	P 
	xor 010h		;f8d9	ee 10 	. . 
	call pe,sub_ec50h		;f8db	ec 50 ec 	. P . 
	ld de,05100h		;f8de	11 00 51 	. . Q 
	nop			;f8e1	00 	. 
	djnz $-18		;f8e2	10 ec 	. . 
	ld de,010ech		;f8e4	11 ec 10 	. . . 
	call pe,0ec10h		;f8e7	ec 10 ec 	. . . 
lf8eah:
	ld de,011eeh		;f8ea	11 ee 11 	. . . 
	call pe,0ec10h		;f8ed	ec 10 ec 	. . . 
	ld d,c			;f8f0	51 	Q 
	call pe,0ec10h		;f8f1	ec 10 ec 	. . . 
	ld d,e			;f8f4	53 	S 
	call pe,0ec10h		;f8f5	ec 10 ec 	. . . 
	djnz $-16		;f8f8	10 ee 	. . 
	djnz $-18		;f8fa	10 ec 	. . 
	djnz lf8eah		;f8fc	10 ec 	. . 
	djnz lf90ch		;f8fe	10 0c 	. . 
	ld (de),a			;f900	12 	. 
	ex af,af'			;f901	08 	. 
	ld (de),a			;f902	12 	. 
	xor h			;f903	ac 	. 
lf904h:
	ld (de),a			;f904	12 	. 
	xor 012h		;f905	ee 12 	. . 
	xor (hl)			;f907	ae 	. 
	djnz lf8b6h		;f908	10 ac 	. . 
	ld (de),a			;f90a	12 	. 
	xor (hl)			;f90b	ae 	. 
lf90ch:
	inc de			;f90c	13 	. 
	xor (hl)			;f90d	ae 	. 
	ld (de),a			;f90e	12 	. 
	xor 012h		;f90f	ee 12 	. . 
	xor h			;f911	ac 	. 
	ld (de),a			;f912	12 	. 
	xor (hl)			;f913	ae 	. 
	ld (de),a			;f914	12 	. 
	xor (hl)			;f915	ae 	. 
lf916h:
	ld (de),a			;f916	12 	. 
	xor h			;f917	ac 	. 
	ld (de),a			;f918	12 	. 
	xor (hl)			;f919	ae 	. 
lf91ah:
	ld (de),a			;f91a	12 	. 
	xor (hl)			;f91b	ae 	. 
	ld (de),a			;f91c	12 	. 
	xor h			;f91d	ac 	. 
	ld (de),a			;f91e	12 	. 
	nop			;f91f	00 	. 
	ld (de),a			;f920	12 	. 
	nop			;f921	00 	. 
	djnz $-18		;f922	10 ec 	. . 
	ld (de),a			;f924	12 	. 
	xor h			;f925	ac 	. 
lf926h:
	ld (de),a			;f926	12 	. 
	xor (hl)			;f927	ae 	. 
	ld (de),a			;f928	12 	. 
	xor (hl)			;f929	ae 	. 
lf92ah:
	djnz lf91ah		;f92a	10 ee 	. . 
	ld (de),a			;f92c	12 	. 
	xor (hl)			;f92d	ae 	. 
	ld (de),a			;f92e	12 	. 
	xor (hl)			;f92f	ae 	. 
	ld (de),a			;f930	12 	. 
	xor (hl)			;f931	ae 	. 
	ld (de),a			;f932	12 	. 
	xor h			;f933	ac 	. 
	ld (de),a			;f934	12 	. 
	xor (hl)			;f935	ae 	. 
	djnz $-80		;f936	10 ae 	. . 
	ld (de),a			;f938	12 	. 
	xor (hl)			;f939	ae 	. 
	djnz lf8eah		;f93a	10 ae 	. . 
	ld (de),a			;f93c	12 	. 
	xor 010h		;f93d	ee 10 	. . 
	adc a,h			;f93f	8c 	. 
	ld (de),a			;f940	12 	. 
	adc a,(hl)			;f941	8e 	. 
	ld (de),a			;f942	12 	. 
	xor (hl)			;f943	ae 	. 
	ld (de),a			;f944	12 	. 
	xor 012h		;f945	ee 12 	. . 
	xor (hl)			;f947	ae 	. 
	ld (de),a			;f948	12 	. 
	xor (hl)			;f949	ae 	. 
	ld (de),a			;f94a	12 	. 
	xor (hl)			;f94b	ae 	. 
	ld de,010aeh		;f94c	11 ae 10 	. . . 
	xor (hl)			;f94f	ae 	. 
	ld (de),a			;f950	12 	. 
	xor (hl)			;f951	ae 	. 
	ld (de),a			;f952	12 	. 
	xor h			;f953	ac 	. 
	djnz lf904h		;f954	10 ae 	. . 
	djnz $-80		;f956	10 ae 	. . 
	ld (de),a			;f958	12 	. 
	xor 012h		;f959	ee 12 	. . 
	xor h			;f95b	ac 	. 
	ld (de),a			;f95c	12 	. 
	xor (hl)			;f95d	ae 	. 
	djnz lf960h		;f95e	10 00 	. . 
lf960h:
	ld (de),a			;f960	12 	. 
	nop			;f961	00 	. 
	ld (de),a			;f962	12 	. 
	xor (hl)			;f963	ae 	. 
	ld (de),a			;f964	12 	. 
	xor (hl)			;f965	ae 	. 
	djnz lf916h		;f966	10 ae 	. . 
	ld (de),a			;f968	12 	. 
	xor (hl)			;f969	ae 	. 
	djnz lf91ah		;f96a	10 ae 	. . 
	ld (de),a			;f96c	12 	. 
	xor (hl)			;f96d	ae 	. 
	ld (de),a			;f96e	12 	. 
	xor h			;f96f	ac 	. 
	ld (de),a			;f970	12 	. 
	xor (hl)			;f971	ae 	. 
	ld (de),a			;f972	12 	. 
	xor (hl)			;f973	ae 	. 
	ld (de),a			;f974	12 	. 
	xor (hl)			;f975	ae 	. 
	djnz lf926h		;f976	10 ae 	. . 
	ld (de),a			;f978	12 	. 
	xor (hl)			;f979	ae 	. 
	djnz lf92ah		;f97a	10 ae 	. . 
	ld (de),a			;f97c	12 	. 
	xor (hl)			;f97d	ae 	. 
	djnz lf90ch		;f97e	10 8c 	. . 
	ld (de),a			;f980	12 	. 
	ex af,af'			;f981	08 	. 
	ld (de),a			;f982	12 	. 
	xor 012h		;f983	ee 12 	. . 
	xor 012h		;f985	ee 12 	. . 
	xor (hl)			;f987	ae 	. 
	ld (de),a			;f988	12 	. 
	adc a,h			;f989	8c 	. 
	ld (de),a			;f98a	12 	. 
	xor 012h		;f98b	ee 12 	. . 
	xor 012h		;f98d	ee 12 	. . 
	xor 052h		;f98f	ee 52 	. R 
	xor 012h		;f991	ee 12 	. . 
	xor (hl)			;f993	ae 	. 
	ld (de),a			;f994	12 	. 
	adc a,012h		;f995	ce 12 	. . 
	xor (hl)			;f997	ae 	. 
	ld (de),a			;f998	12 	. 
	xor 012h		;f999	ee 12 	. . 
	xor 012h		;f99b	ee 12 	. . 
	xor 012h		;f99d	ee 12 	. . 
	ex af,af'			;f99f	08 	. 
	ld (de),a			;f9a0	12 	. 
	nop			;f9a1	00 	. 
	ld (de),a			;f9a2	12 	. 
	xor 012h		;f9a3	ee 12 	. . 
	xor 012h		;f9a5	ee 12 	. . 
	xor 012h		;f9a7	ee 12 	. . 
	xor 012h		;f9a9	ee 12 	. . 
	xor (hl)			;f9ab	ae 	. 
	ld (de),a			;f9ac	12 	. 
	xor 012h		;f9ad	ee 12 	. . 
	xor h			;f9af	ac 	. 
	ld (de),a			;f9b0	12 	. 
	xor 012h		;f9b1	ee 12 	. . 
	xor 052h		;f9b3	ee 52 	. R 
	xor (hl)			;f9b5	ae 	. 
	ld (de),a			;f9b6	12 	. 
	xor 012h		;f9b7	ee 12 	. . 
	xor 012h		;f9b9	ee 12 	. . 
	xor 012h		;f9bb	ee 12 	. . 
	xor 012h		;f9bd	ee 12 	. . 
	adc a,(hl)			;f9bf	8e 	. 
	ld (de),a			;f9c0	12 	. 
	adc a,(hl)			;f9c1	8e 	. 
	ld (de),a			;f9c2	12 	. 
	xor 012h		;f9c3	ee 12 	. . 
	xor 012h		;f9c5	ee 12 	. . 
	xor 012h		;f9c7	ee 12 	. . 
	xor 012h		;f9c9	ee 12 	. . 
	adc a,(hl)			;f9cb	8e 	. 
	ld (de),a			;f9cc	12 	. 
	xor (hl)			;f9cd	ae 	. 
	ld (de),a			;f9ce	12 	. 
	xor (hl)			;f9cf	ae 	. 
	ld (de),a			;f9d0	12 	. 
	xor (hl)			;f9d1	ae 	. 
	ld (de),a			;f9d2	12 	. 
	adc a,012h		;f9d3	ce 12 	. . 
	xor 010h		;f9d5	ee 10 	. . 
	xor (hl)			;f9d7	ae 	. 
	ld (de),a			;f9d8	12 	. 
	xor 012h		;f9d9	ee 12 	. . 
	xor 012h		;f9db	ee 12 	. . 
	xor 012h		;f9dd	ee 12 	. . 
	nop			;f9df	00 	. 
	ld (de),a			;f9e0	12 	. 
	nop			;f9e1	00 	. 
	ld (de),a			;f9e2	12 	. 
	xor (hl)			;f9e3	ae 	. 
	ld (de),a			;f9e4	12 	. 
	xor 010h		;f9e5	ee 10 	. . 
	xor 012h		;f9e7	ee 12 	. . 
	xor 012h		;f9e9	ee 12 	. . 
	xor 012h		;f9eb	ee 12 	. . 
	xor 012h		;f9ed	ee 12 	. . 
	xor 012h		;f9ef	ee 12 	. . 
	xor (hl)			;f9f1	ae 	. 
	ld (de),a			;f9f2	12 	. 
	xor 012h		;f9f3	ee 12 	. . 
	xor 012h		;f9f5	ee 12 	. . 
	xor 012h		;f9f7	ee 12 	. . 
	xor 012h		;f9f9	ee 12 	. . 
	xor 012h		;f9fb	ee 12 	. . 
	xor 012h		;f9fd	ee 12 	. . 
	adc a,h			;f9ff	8c 	. 
	inc de			;fa00	13 	. 
	ex af,af'			;fa01	08 	. 
	ld sp,011ach		;fa02	31 ac 11 	1 . . 
	xor h			;fa05	ac 	. 
lfa06h:
	ld de,011ach		;fa06	11 ac 11 	. . . 
	xor h			;fa09	ac 	. 
	ld de,011ach		;fa0a	11 ac 11 	. . . 
	xor h			;fa0d	ac 	. 
	jr nc,$-82		;fa0e	30 ac 	0 . 
	ld de,011ach		;fa10	11 ac 11 	. . . 
	xor h			;fa13	ac 	. 
	inc sp			;fa14	33 	3 
	xor h			;fa15	ac 	. 
	ld de,013ach		;fa16	11 ac 13 	. . . 
	xor h			;fa19	ac 	. 
	djnz $-82		;fa1a	10 ac 	. . 
lfa1ch:
	ld de,010ach		;fa1c	11 ac 10 	. . . 
	nop			;fa1f	00 	. 
	ld de,01100h		;fa20	11 00 11 	. . . 
	xor h			;fa23	ac 	. 
	ld de,011ach		;fa24	11 ac 11 	. . . 
	xor h			;fa27	ac 	. 
	ld sp,031ach		;fa28	31 ac 31 	1 . 1 
	xor h			;fa2b	ac 	. 
	ld de,011ach		;fa2c	11 ac 11 	. . . 
	xor h			;fa2f	ac 	. 
	ld de,011ach		;fa30	11 ac 11 	. . . 
	xor h			;fa33	ac 	. 
	ld de,011ach		;fa34	11 ac 11 	. . . 
	xor h			;fa37	ac 	. 
	ld de,011ach		;fa38	11 ac 11 	. . . 
	xor h			;fa3b	ac 	. 
	ld de,010eeh		;fa3c	11 ee 10 	. . . 
	xor h			;fa3f	ac 	. 
	ld de,011ach		;fa40	11 ac 11 	. . . 
	xor h			;fa43	ac 	. 
	ld de,011ech		;fa44	11 ec 11 	. . . 
	call pe,0ac12h		;fa47	ec 12 ac 	. . . 
	ld de,011ach		;fa4a	11 ac 11 	. . . 
	xor h			;fa4d	ac 	. 
	ld de,011ach		;fa4e	11 ac 11 	. . . 
	xor 010h		;fa51	ee 10 	. . 
	xor h			;fa53	ac 	. 
	ld de,011ach		;fa54	11 ac 11 	. . . 
	xor h			;fa57	ac 	. 
	djnz lfa06h		;fa58	10 ac 	. . 
	djnz $-82		;fa5a	10 ac 	. . 
lfa5ch:
	inc de			;fa5c	13 	. 
	call pe,0x0010		;fa5d	ec 10 00 	. . . 
	ld sp,01100h		;fa60	31 00 11 	1 . . 
	xor h			;fa63	ac 	. 
	ld de,010ach		;fa64	11 ac 10 	. . . 
	xor h			;fa67	ac 	. 
	ld de,010ach		;fa68	11 ac 10 	. . . 
	xor h			;fa6b	ac 	. 
	djnz lfa5ch		;fa6c	10 ee 	. . 
	djnz lfa1ch		;fa6e	10 ac 	. . 
	ld de,011aeh		;fa70	11 ae 11 	. . . 
	xor h			;fa73	ac 	. 
	ld sp,011ech		;fa74	31 ec 11 	1 . . 
	xor h			;fa77	ac 	. 
	ld de,011ach		;fa78	11 ac 11 	. . . 
	xor h			;fa7b	ac 	. 
	ld de,010ach		;fa7c	11 ac 10 	. . . 
	adc a,b			;fa7f	88 	. 
	ld d,e			;fa80	53 	S 
	ex af,af'			;fa81	08 	. 
	ld d,d			;fa82	52 	R 
	xor 053h		;fa83	ee 53 	. S 
	xor 053h		;fa85	ee 53 	. S 
	xor 053h		;fa87	ee 53 	. S 
	xor 052h		;fa89	ee 52 	. R 
	xor 052h		;fa8b	ee 52 	. R 
	xor 053h		;fa8d	ee 53 	. S 
	xor 053h		;fa8f	ee 53 	. S 
	xor 052h		;fa91	ee 52 	. R 
	xor 073h		;fa93	ee 73 	. s 
	xor 052h		;fa95	ee 52 	. R 
	call pe,sub_ee53h		;fa97	ec 53 ee 	. S . 
	ld d,d			;fa9a	52 	R 
	xor 013h		;fa9b	ee 13 	. . 
	xor 052h		;fa9d	ee 52 	. R 
	nop			;fa9f	00 	. 
	ld d,e			;faa0	53 	S 
	nop			;faa1	00 	. 
	ld d,d			;faa2	52 	R 
	call pe,sub_ee53h		;faa3	ec 53 ee 	. S . 
	ld d,d			;faa6	52 	R 
	xor 053h		;faa7	ee 53 	. S 
	xor 053h		;faa9	ee 53 	. S 
	call pe,sub_ee52h		;faab	ec 52 ee 	. R . 
	ld (de),a			;faae	12 	. 
	xor 053h		;faaf	ee 53 	. S 
	xor 053h		;fab1	ee 53 	. S 
	xor 053h		;fab3	ee 53 	. S 
	xor 052h		;fab5	ee 52 	. R 
	xor 053h		;fab7	ee 53 	. S 
	xor 052h		;fab9	ee 52 	. R 
	xor 053h		;fabb	ee 53 	. S 
	xor 010h		;fabd	ee 10 	. . 
	adc a,053h		;fabf	ce 53 	. S 
	adc a,052h		;fac1	ce 52 	. R 
	xor 053h		;fac3	ee 53 	. S 
	xor 052h		;fac5	ee 52 	. R 
	xor 053h		;fac7	ee 53 	. S 
	xor 052h		;fac9	ee 52 	. R 
	xor 053h		;facb	ee 53 	. S 
	xor 052h		;facd	ee 52 	. R 
	call pe,sub_ee53h		;facf	ec 53 ee 	. S . 
	ld d,d			;fad2	52 	R 
	xor 053h		;fad3	ee 53 	. S 
	xor 052h		;fad5	ee 52 	. R 
	xor 052h		;fad7	ee 52 	. R 
	xor 052h		;fad9	ee 52 	. R 
	xor 053h		;fadb	ee 53 	. S 
	xor 052h		;fadd	ee 52 	. R 
	nop			;fadf	00 	. 
	ld d,e			;fae0	53 	S 
	nop			;fae1	00 	. 
	ld d,d			;fae2	52 	R 
	xor 013h		;fae3	ee 13 	. . 
	xor 012h		;fae5	ee 12 	. . 
	xor 053h		;fae7	ee 53 	. S 
	xor 012h		;fae9	ee 12 	. . 
	xor 053h		;faeb	ee 53 	. S 
	xor 012h		;faed	ee 12 	. . 
	xor 013h		;faef	ee 13 	. . 
	xor 012h		;faf1	ee 12 	. . 
	call pe,sub_ee52h		;faf3	ec 52 ee 	. R . 
	ld (de),a			;faf6	12 	. 
	xor 052h		;faf7	ee 52 	. R 
	xor 052h		;faf9	ee 52 	. R 
	xor 053h		;fafb	ee 53 	. S 
	xor 052h		;fafd	ee 52 	. R 
	adc a,b			;faff	88 	. 
	inc de			;fb00	13 	. 
	ex af,af'			;fb01	08 	. 
	djnz $-18		;fb02	10 ec 	. . 
	inc de			;fb04	13 	. 
	xor 013h		;fb05	ee 13 	. . 
	xor h			;fb07	ac 	. 
	ld (de),a			;fb08	12 	. 
	call pe,sub_ec12h		;fb09	ec 12 ec 	. . . 
	ld (de),a			;fb0c	12 	. 
	xor (hl)			;fb0d	ae 	. 
	ld (de),a			;fb0e	12 	. 
	xor h			;fb0f	ac 	. 
	inc de			;fb10	13 	. 
	xor 012h		;fb11	ee 12 	. . 
	call pe,0ae12h		;fb13	ec 12 ae 	. . . 
	ld (de),a			;fb16	12 	. 
	xor h			;fb17	ac 	. 
	ld (032ach),a		;fb18	32 ac 32 	2 . 2 
	xor h			;fb1b	ac 	. 
	ld (de),a			;fb1c	12 	. 
	xor 010h		;fb1d	ee 10 	. . 
	nop			;fb1f	00 	. 
	ld (de),a			;fb20	12 	. 
	nop			;fb21	00 	. 
	ld (de),a			;fb22	12 	. 
	xor h			;fb23	ac 	. 
	inc de			;fb24	13 	. 
	call pe,0ec10h		;fb25	ec 10 ec 	. . . 
	inc sp			;fb28	33 	3 
	xor 012h		;fb29	ee 12 	. . 
	xor h			;fb2b	ac 	. 
	ld (de),a			;fb2c	12 	. 
	xor h			;fb2d	ac 	. 
	ld (de),a			;fb2e	12 	. 
	call pe,sub_ee13h		;fb2f	ec 13 ee 	. . . 
	ld (de),a			;fb32	12 	. 
	xor h			;fb33	ac 	. 
	inc de			;fb34	13 	. 
	xor 012h		;fb35	ee 12 	. . 
	xor h			;fb37	ac 	. 
	inc de			;fb38	13 	. 
	xor h			;fb39	ac 	. 
	djnz $-80		;fb3a	10 ae 	. . 
	ld (de),a			;fb3c	12 	. 
	xor 010h		;fb3d	ee 10 	. . 
	adc a,(hl)			;fb3f	8e 	. 
	ld (de),a			;fb40	12 	. 
	adc a,h			;fb41	8c 	. 
	ld (de),a			;fb42	12 	. 
	call pe,0ec10h		;fb43	ec 10 ec 	. . . 
	ld (de),a			;fb46	12 	. 
	xor 013h		;fb47	ee 13 	. . 
	xor 012h		;fb49	ee 12 	. . 
	call pe,sub_ee11h		;fb4b	ec 11 ee 	. . . 
	ld (de),a			;fb4e	12 	. 
	xor h			;fb4f	ac 	. 
	inc de			;fb50	13 	. 
	call pe,0ee30h		;fb51	ec 30 ee 	. 0 . 
	inc de			;fb54	13 	. 
	xor (hl)			;fb55	ae 	. 
	ld (de),a			;fb56	12 	. 
	xor h			;fb57	ac 	. 
	ld (de),a			;fb58	12 	. 
	xor (hl)			;fb59	ae 	. 
	djnz $-18		;fb5a	10 ec 	. . 
	inc de			;fb5c	13 	. 
	xor 052h		;fb5d	ee 52 	. R 
	nop			;fb5f	00 	. 
	inc de			;fb60	13 	. 
	nop			;fb61	00 	. 
	ld (de),a			;fb62	12 	. 
	xor (hl)			;fb63	ae 	. 
	inc de			;fb64	13 	. 
	xor 010h		;fb65	ee 10 	. . 
	xor 012h		;fb67	ee 12 	. . 
	xor 012h		;fb69	ee 12 	. . 
	xor h			;fb6b	ac 	. 
	djnz $-82		;fb6c	10 ac 	. . 
	ld (de),a			;fb6e	12 	. 
	xor 013h		;fb6f	ee 13 	. . 
	xor 010h		;fb71	ee 10 	. . 
	call pe,sub_ec12h		;fb73	ec 12 ec 	. . . 
	ld (de),a			;fb76	12 	. 
	xor (hl)			;fb77	ae 	. 
	ld (de),a			;fb78	12 	. 
	xor 012h		;fb79	ee 12 	. . 
	xor h			;fb7b	ac 	. 
	ld (de),a			;fb7c	12 	. 
	xor 010h		;fb7d	ee 10 	. . 
	ex af,af'			;fb7f	08 	. 
	ld de,01008h		;fb80	11 08 10 	. . . 
	call pe,0ac11h		;fb83	ec 11 ac 	. . . 
	ld de,011ech		;fb86	11 ec 11 	. . . 
	call pe,0ec10h		;fb89	ec 10 ec 	. . . 
lfb8ch:
	ld de,011ech		;fb8c	11 ec 11 	. . . 
	call pe,sub_ec11h		;fb8f	ec 11 ec 	. . . 
	ld d,d			;fb92	52 	R 
	call pe,sub_ec11h		;fb93	ec 11 ec 	. . . 
	djnz $-18		;fb96	10 ec 	. . 
lfb98h:
	ld de,050ech		;fb98	11 ec 50 	. . P 
	call pe,sub_ec11h		;fb9b	ec 11 ec 	. . . 
	djnz lfba0h		;fb9e	10 00 	. . 
lfba0h:
	ld de,01100h		;fba0	11 00 11 	. . . 
	call pe,sub_ec11h		;fba3	ec 11 ec 	. . . 
	ld d,b			;fba6	50 	P 
	call pe,sub_ec53h		;fba7	ec 53 ec 	. S . 
	djnz lfb98h		;fbaa	10 ec 	. . 
	ld de,010ech		;fbac	11 ec 10 	. . . 
	call pe,0ac11h		;fbaf	ec 11 ac 	. . . 
	ld de,051ech		;fbb2	11 ec 51 	. . Q 
	call pe,sub_ec13h		;fbb5	ec 13 ec 	. . . 
	ld de,010ech		;fbb8	11 ec 10 	. . . 
	call pe,sub_ec50h+1		;fbbb	ec 51 ec 	. Q . 
	djnz lfb8ch		;fbbe	10 cc 	. . 
	ld de,0108ch		;fbc0	11 8c 10 	. . . 
	call pe,sub_ec50h		;fbc3	ec 50 ec 	. P . 
	ld de,051ech		;fbc6	11 ec 51 	. . Q 
	call pe,sub_ec50h		;fbc9	ec 50 ec 	. P . 
	djnz $-18		;fbcc	10 ec 	. . 
	ld de,051ech		;fbce	11 ec 51 	. . Q 
	call pe,sub_ec11h		;fbd1	ec 11 ec 	. . . 
lfbd4h:
	ld de,011ech		;fbd4	11 ec 11 	. . . 
	xor h			;fbd7	ac 	. 
	ld d,c			;fbd8	51 	Q 
	call pe,sub_ec11h		;fbd9	ec 11 ec 	. . . 
	ld de,050ech		;fbdc	11 ec 50 	. . P 
	nop			;fbdf	00 	. 
	ld de,VIDEO_ram		;fbe0	11 00 50 	. . P 
	call pe,sub_ec11h		;fbe3	ec 11 ec 	. . . 
	djnz lfbd4h		;fbe6	10 ec 	. . 
lfbe8h:
	ld de,011ech		;fbe8	11 ec 11 	. . . 
	call pe,sub_ec50h+1		;fbeb	ec 51 ec 	. Q . 
	ld d,b			;fbee	50 	P 
	call pe,sub_ec11h		;fbef	ec 11 ec 	. . . 
	ld de,011ech		;fbf2	11 ec 11 	. . . 
	call pe,0ec10h		;fbf5	ec 10 ec 	. . . 
lfbf8h:
	ld de,050ech		;fbf8	11 ec 50 	. . P 
	call pe,0ec10h		;fbfb	ec 10 ec 	. . . 
	djnz $-118		;fbfe	10 88 	. . 
	inc de			;fc00	13 	. 
	ex af,af'			;fc01	08 	. 
	djnz $-82		;fc02	10 ac 	. . 
	inc de			;fc04	13 	. 
	xor h			;fc05	ac 	. 
	inc sp			;fc06	33 	3 
	xor h			;fc07	ac 	. 
	ld sp,013aeh		;fc08	31 ae 13 	1 . . 
	xor h			;fc0b	ac 	. 
	inc sp			;fc0c	33 	3 
	xor h			;fc0d	ac 	. 
	inc sp			;fc0e	33 	3 
	xor h			;fc0f	ac 	. 
	inc sp			;fc10	33 	3 
	xor 033h		;fc11	ee 33 	. 3 
	xor h			;fc13	ac 	. 
	inc sp			;fc14	33 	3 
	call pe,0ac32h		;fc15	ec 32 ac 	. 2 . 
	ld sp,013ach		;fc18	31 ac 13 	1 . . 
	xor h			;fc1b	ac 	. 
	inc de			;fc1c	13 	. 
	call pe,00030h		;fc1d	ec 30 00 	. 0 . 
	inc sp			;fc20	33 	3 
	nop			;fc21	00 	. 
	inc sp			;fc22	33 	3 
	xor h			;fc23	ac 	. 
	inc de			;fc24	13 	. 
	call pe,0ac13h		;fc25	ec 13 ac 	. . . 
	inc de			;fc28	13 	. 
	xor h			;fc29	ac 	. 
	ld sp,031ach		;fc2a	31 ac 31 	1 . 1 
	xor h			;fc2d	ac 	. 
	ld sp,033ach		;fc2e	31 ac 33 	1 . 3 
	xor h			;fc31	ac 	. 
	ld sp,011ach		;fc32	31 ac 11 	1 . . 
	xor h			;fc35	ac 	. 
	ld de,013ach		;fc36	11 ac 13 	. . . 
	xor (hl)			;fc39	ae 	. 
	djnz lfbe8h		;fc3a	10 ac 	. . 
	inc de			;fc3c	13 	. 
	call pe,0ac31h		;fc3d	ec 31 ac 	. 1 . 
	ld sp,011ach		;fc40	31 ac 11 	1 . . 
	xor h			;fc43	ac 	. 
	ld sp,031aeh		;fc44	31 ae 31 	1 . 1 
	xor (hl)			;fc47	ae 	. 
	inc de			;fc48	13 	. 
	xor h			;fc49	ac 	. 
	djnz lfbf8h		;fc4a	10 ac 	. . 
	inc de			;fc4c	13 	. 
	xor (hl)			;fc4d	ae 	. 
lfc4eh:
	ld de,031aeh		;fc4e	11 ae 31 	. . 1 
	xor h			;fc51	ac 	. 
	ld de,011ech		;fc52	11 ec 11 	. . . 
	call pe,0ac11h		;fc55	ec 11 ac 	. . . 
	ld de,010ach		;fc58	11 ac 10 	. . . 
	xor h			;fc5b	ac 	. 
	ld de,031ach		;fc5c	11 ac 31 	. . 1 
	nop			;fc5f	00 	. 
	ld sp,01300h		;fc60	31 00 13 	1 . . 
	xor h			;fc63	ac 	. 
	inc sp			;fc64	33 	3 
	xor h			;fc65	ac 	. 
	ld sp,011ech		;fc66	31 ec 11 	1 . . 
	xor h			;fc69	ac 	. 
	ld de,011ach		;fc6a	11 ac 11 	. . . 
	xor 033h		;fc6d	ee 33 	. 3 
	xor h			;fc6f	ac 	. 
	inc de			;fc70	13 	. 
	call pe,0ac31h		;fc71	ec 31 ac 	. 1 . 
	ld d,e			;fc74	53 	S 
	xor h			;fc75	ac 	. 
lfc76h:
	ld sp,033ach		;fc76	31 ac 33 	1 . 3 
	xor 011h		;fc79	ee 11 	. . 
	xor 013h		;fc7b	ee 13 	. . 
	call pe,08810h		;fc7d	ec 10 88 	. . . 
	ld de,01188h		;fc80	11 88 11 	. . . 
	call pe,sub_ec11h		;fc83	ec 11 ec 	. . . 
	ld (de),a			;fc86	12 	. 
	xor h			;fc87	ac 	. 
	ld de,011ech		;fc88	11 ec 11 	. . . 
	call pe,sub_ec11h		;fc8b	ec 11 ec 	. . . 
	djnz $-18		;fc8e	10 ec 	. . 
	ld de,011ach		;fc90	11 ac 11 	. . . 
	xor h			;fc93	ac 	. 
	inc de			;fc94	13 	. 
	call pe,0ac10h		;fc95	ec 10 ac 	. . . 
	ld de,013ach		;fc98	11 ac 13 	. . . 
	call pe,sub_ec11h		;fc9b	ec 11 ec 	. . . 
	djnz lfca0h		;fc9e	10 00 	. . 
lfca0h:
	inc de			;fca0	13 	. 
	nop			;fca1	00 	. 
	ld de,013ach		;fca2	11 ac 13 	. . . 
	call pe,0ac11h		;fca5	ec 11 ac 	. . . 
	inc de			;fca8	13 	. 
	call pe,0ec10h		;fca9	ec 10 ec 	. . . 
	ld de,011ech		;fcac	11 ec 11 	. . . 
	xor h			;fcaf	ac 	. 
	ld de,012ach		;fcb0	11 ac 12 	. . . 
	xor h			;fcb3	ac 	. 
lfcb4h:
	ld de,011ach		;fcb4	11 ac 11 	. . . 
	xor h			;fcb7	ac 	. 
	ld de,011ach		;fcb8	11 ac 11 	. . . 
	xor h			;fcbb	ac 	. 
lfcbch:
	inc de			;fcbc	13 	. 
	call pe,08c10h		;fcbd	ec 10 8c 	. . . 
	djnz lfc4eh		;fcc0	10 8c 	. . 
	ld de,011ach		;fcc2	11 ac 11 	. . . 
	call pe,0ac11h		;fcc5	ec 11 ac 	. . . 
	djnz lfc76h		;fcc8	10 ac 	. . 
	ld de,011ech		;fcca	11 ec 11 	. . . 
	call pe,sub_ec11h		;fccd	ec 11 ec 	. . . 
	ld de,011ech		;fcd0	11 ec 11 	. . . 
	xor h			;fcd3	ac 	. 
	ld de,011ech		;fcd4	11 ec 11 	. . . 
	xor h			;fcd7	ac 	. 
	inc de			;fcd8	13 	. 
	call pe,0ac11h		;fcd9	ec 11 ac 	. . . 
	ld de,011ech		;fcdc	11 ec 11 	. . . 
	nop			;fcdf	00 	. 
lfce0h:
	inc de			;fce0	13 	. 
	nop			;fce1	00 	. 
	ld de,013ach		;fce2	11 ac 13 	. . . 
	call pe,0ac11h		;fce5	ec 11 ac 	. . . 
lfce8h:
	ld de,011ech		;fce8	11 ec 11 	. . . 
	xor h			;fceb	ac 	. 
	ld de,011ech		;fcec	11 ec 11 	. . . 
	xor h			;fcef	ac 	. 
	ld d,c			;fcf0	51 	Q 
	call pe,0ec10h		;fcf1	ec 10 ec 	. . . 
	ld de,010ech		;fcf4	11 ec 10 	. . . 
	call pe,sub_ec11h		;fcf7	ec 11 ec 	. . . 
	djnz lfce8h		;fcfa	10 ec 	. . 
	ld de,010ech		;fcfc	11 ec 10 	. . . 
	adc a,b			;fcff	88 	. 
	ld de,Cartridge_ROM		;fd00	11 00 10 	. . . 
	call pe,sub_ec11h		;fd03	ec 11 ec 	. . . 
	djnz lfcb4h		;fd06	10 ac 	. . 
	ld de,011ech		;fd08	11 ec 11 	. . . 
	call pe,0ac11h		;fd0b	ec 11 ac 	. . . 
	djnz lfcbch		;fd0e	10 ac 	. . 
	djnz $-18		;fd10	10 ec 	. . 
	ld de,011ach		;fd12	11 ac 11 	. . . 
	xor h			;fd15	ac 	. 
	ld de,011ach		;fd16	11 ac 11 	. . . 
	call pe,sub_ec11h		;fd19	ec 11 ec 	. . . 
	ld de,011ech		;fd1c	11 ec 11 	. . . 
	nop			;fd1f	00 	. 
	ld de,Cartridge_ROM		;fd20	11 00 10 	. . . 
	xor h			;fd23	ac 	. 
	ld de,031ech		;fd24	11 ec 31 	. . 1 
	xor h			;fd27	ac 	. 
	ld sp,010ach		;fd28	31 ac 10 	1 . . 
	call pe,0ac11h		;fd2b	ec 11 ac 	. . . 
	ld de,011ach		;fd2e	11 ac 11 	. . . 
	xor h			;fd31	ac 	. 
	djnz lfce0h		;fd32	10 ac 	. . 
	ld de,010ech		;fd34	11 ec 10 	. . . 
	xor h			;fd37	ac 	. 
	ld de,010ach		;fd38	11 ac 10 	. . . 
	xor h			;fd3b	ac 	. 
	ld de,011ech		;fd3c	11 ec 11 	. . . 
	adc a,h			;fd3f	8c 	. 
	ld de,0318ch		;fd40	11 8c 31 	. . 1 
	xor h			;fd43	ac 	. 
	ld de,010ach		;fd44	11 ac 10 	. . . 
	xor h			;fd47	ac 	. 
	ld de,011ach		;fd48	11 ac 11 	. . . 
	xor h			;fd4b	ac 	. 
	ld de,010ech		;fd4c	11 ec 10 	. . . 
	xor h			;fd4f	ac 	. 
	ld de,011ech		;fd50	11 ec 11 	. . . 
	xor h			;fd53	ac 	. 
	ld de,011ech		;fd54	11 ec 11 	. . . 
	call pe,sub_ec11h		;fd57	ec 11 ec 	. . . 
	ld de,011ech		;fd5a	11 ec 11 	. . . 
	call pe,00011h		;fd5d	ec 11 00 	. . . 
	ld de,01100h		;fd60	11 00 11 	. . . 
	xor h			;fd63	ac 	. 
	ld de,010ach		;fd64	11 ac 10 	. . . 
	xor h			;fd67	ac 	. 
	ld de,011ech		;fd68	11 ec 11 	. . . 
	xor h			;fd6b	ac 	. 
	ld de,011ech		;fd6c	11 ec 11 	. . . 
	xor h			;fd6f	ac 	. 
	ld de,010ech		;fd70	11 ec 10 	. . . 
	call pe,0ac11h		;fd73	ec 11 ac 	. . . 
	ld de,011ach		;fd76	11 ac 11 	. . . 
	call pe,sub_ec11h		;fd79	ec 11 ec 	. . . 
	ld de,010ech		;fd7c	11 ec 10 	. . . 
	ex af,af'			;fd7f	08 	. 
	ld d,e			;fd80	53 	S 
	ex af,af'			;fd81	08 	. 
	inc de			;fd82	13 	. 
	xor h			;fd83	ac 	. 
	inc de			;fd84	13 	. 
	call pe,sub_ec13h		;fd85	ec 13 ec 	. . . 
	inc de			;fd88	13 	. 
	call pe,sub_ec53h		;fd89	ec 53 ec 	. S . 
	ld d,e			;fd8c	53 	S 
	call pe,0ac13h		;fd8d	ec 13 ac 	. . . 
	ld d,e			;fd90	53 	S 
	call pe,sub_ec13h		;fd91	ec 13 ec 	. . . 
	inc de			;fd94	13 	. 
	adc a,053h		;fd95	ce 53 	. S 
	xor 053h		;fd97	ee 53 	. S 
	xor 053h		;fd99	ee 53 	. S 
	xor 053h		;fd9b	ee 53 	. S 
	xor 053h		;fd9d	ee 53 	. S 
	nop			;fd9f	00 	. 
	ld d,e			;fda0	53 	S 
	nop			;fda1	00 	. 
	ld d,e			;fda2	53 	S 
	call pe,0ce13h		;fda3	ec 13 ce 	. . . 
	ld d,e			;fda6	53 	S 
	xor h			;fda7	ac 	. 
	ld d,e			;fda8	53 	S 
	xor 053h		;fda9	ee 53 	. S 
	call pe,sub_ee13h		;fdab	ec 13 ee 	. . . 
	inc de			;fdae	13 	. 
	call pe,sub_ee13h		;fdaf	ec 13 ee 	. . . 
	ld de,053ech		;fdb2	11 ec 53 	. . S 
	xor 013h		;fdb5	ee 13 	. . 
	call pe,sub_ed13h		;fdb7	ec 13 ed 	. . . 
	ld d,e			;fdba	53 	S 
	xor 013h		;fdbb	ee 13 	. . 
	xor h			;fdbd	ac 	. 
	inc de			;fdbe	13 	. 
	call z,0ac13h		;fdbf	cc 13 ac 	. . . 
	ld d,e			;fdc2	53 	S 
	xor 053h		;fdc3	ee 53 	. S 
	xor h			;fdc5	ac 	. 
	ld d,e			;fdc6	53 	S 
	call pe,sub_ee53h		;fdc7	ec 53 ee 	. S . 
	inc de			;fdca	13 	. 
	call pe,sub_ee53h		;fdcb	ec 53 ee 	. S . 
	ld d,e			;fdce	53 	S 
	call pe,sub_ee13h		;fdcf	ec 13 ee 	. . . 
	ld d,e			;fdd2	53 	S 
	call pe,sub_ee13h		;fdd3	ec 13 ee 	. . . 
	ld d,e			;fdd6	53 	S 
	call pe,sub_ee53h		;fdd7	ec 53 ee 	. S . 
	ld d,e			;fdda	53 	S 
	call pe,sub_ee53h		;fddb	ec 53 ee 	. S . 
	inc de			;fdde	13 	. 
	nop			;fddf	00 	. 
	inc de			;fde0	13 	. 
	nop			;fde1	00 	. 
	ld d,e			;fde2	53 	S 
	adc a,h			;fde3	8c 	. 
	ld d,e			;fde4	53 	S 
	xor 053h		;fde5	ee 53 	. S 
	xor 013h		;fde7	ee 13 	. . 
	call pe,sub_ee53h		;fde9	ec 53 ee 	. S . 
	ld d,e			;fdec	53 	S 
	xor 011h		;fded	ee 11 	. . 
	call pe,sub_ec13h		;fdef	ec 13 ec 	. . . 
	inc de			;fdf2	13 	. 
	call pe,sub_ec53h		;fdf3	ec 53 ec 	. S . 
	ld d,e			;fdf6	53 	S 
	xor 013h		;fdf7	ee 13 	. . 
	xor 013h		;fdf9	ee 13 	. . 
	xor (hl)			;fdfb	ae 	. 
	inc de			;fdfc	13 	. 
	rst 28h			;fdfd	ef 	. 
	ld de,07b8ch		;fdfe	11 8c 7b 	. . { 
	ex af,af'			;fe01	08 	. 
	inc sp			;fe02	33 	3 
	call pe,sub_ec73h		;fe03	ec 73 ec 	. s . 
	inc de			;fe06	13 	. 
	xor h			;fe07	ac 	. 
	inc sp			;fe08	33 	3 
	rst 28h			;fe09	ef 	. 
	inc sp			;fe0a	33 	3 
	call pe,0ae13h		;fe0b	ec 13 ae 	. . . 
	inc de			;fe0e	13 	. 
	xor (hl)			;fe0f	ae 	. 
	ld (hl),e			;fe10	73 	s 
	call pe,sub_ec33h		;fe11	ec 33 ec 	. 3 . 
	ld (hl),e			;fe14	73 	s 
	call pe,sub_ec53h		;fe15	ec 53 ec 	. S . 
	inc sp			;fe18	33 	3 
	call pe,sub_ec33h		;fe19	ec 33 ec 	. 3 . 
	ld (hl),e			;fe1c	73 	s 
	call pe,00033h		;fe1d	ec 33 00 	. 3 . 
	inc sp			;fe20	33 	3 
	nop			;fe21	00 	. 
	inc sp			;fe22	33 	3 
	call pe,0ad33h		;fe23	ec 33 ad 	. 3 . 
	ld (hl),e			;fe26	73 	s 
	call pe,sub_ec73h		;fe27	ec 73 ec 	. s . 
	inc sp			;fe2a	33 	3 
	call pe,sub_ef73h		;fe2b	ec 73 ef 	. s . 
	inc sp			;fe2e	33 	3 
	xor l			;fe2f	ad 	. 
	ld (hl),e			;fe30	73 	s 
	defb 0edh;next byte illegal after ed		;fe31	ed 	. 
	inc sp			;fe32	33 	3 
	call pe,0ad73h		;fe33	ec 73 ad 	. s . 
	ld (hl),e			;fe36	73 	s 
	call pe,0ee51h		;fe37	ec 51 ee 	. Q . 
	ld (hl),c			;fe3a	71 	q 
	call pe,sub_ef31h+2		;fe3b	ec 33 ef 	. 3 . 
	ld sp,033ach		;fe3e	31 ac 33 	1 . 3 
	adc a,h			;fe41	8c 	. 
	ld (hl),e			;fe42	73 	s 
	call pe,sub_ef73h		;fe43	ec 73 ef 	. s . 
	ld (hl),e			;fe46	73 	s 
	xor h			;fe47	ac 	. 
	inc de			;fe48	13 	. 
	xor 033h		;fe49	ee 33 	. 3 
	xor 073h		;fe4b	ee 73 	. s 
	call pe,sub_ec33h		;fe4d	ec 33 ec 	. 3 . 
	ld (hl),e			;fe50	73 	s 
	xor 073h		;fe51	ee 73 	. s 
	call pe,sub_ec33h		;fe53	ec 33 ec 	. 3 . 
	inc sp			;fe56	33 	3 
	call pe,led73h		;fe57	ec 73 ed 	. s . 
	ld d,e			;fe5a	53 	S 
	xor 073h		;fe5b	ee 73 	. s 
	call pe,00033h		;fe5d	ec 33 00 	. 3 . 
	inc de			;fe60	13 	. 
	nop			;fe61	00 	. 
	ld (hl),e			;fe62	73 	s 
	xor 033h		;fe63	ee 33 	. 3 
	xor (hl)			;fe65	ae 	. 
	inc de			;fe66	13 	. 
	xor 033h		;fe67	ee 33 	. 3 
	defb 0edh;next byte illegal after ed		;fe69	ed 	. 
	ld sp,073ech		;fe6a	31 ec 73 	1 . s 
	defb 0edh;next byte illegal after ed		;fe6d	ed 	. 
	inc de			;fe6e	13 	. 
	call pe,sub_ec73h		;fe6f	ec 73 ec 	. s . 
	inc sp			;fe72	33 	3 
	call pe,sub_ee73h		;fe73	ec 73 ee 	. s . 
	inc de			;fe76	13 	. 
	call pe,sub_ec73h		;fe77	ec 73 ec 	. s . 
	ld de,013aeh		;fe7a	11 ae 13 	. . . 
	xor 013h		;fe7d	ee 13 	. . 
	adc a,b			;fe7f	88 	. 
	ld d,e			;fe80	53 	S 
	ex af,af'			;fe81	08 	. 
	ld de,073ech		;fe82	11 ec 73 	. . s 
	call pe,0ac31h		;fe85	ec 31 ac 	. 1 . 
	inc de			;fe88	13 	. 
	call pe,sub_ec50h+1		;fe89	ec 51 ec 	. Q . 
	ld d,e			;fe8c	53 	S 
	call pe,sub_ec73h		;fe8d	ec 73 ec 	. s . 
	ld (hl),e			;fe90	73 	s 
	xor h			;fe91	ac 	. 
	ld (hl),e			;fe92	73 	s 
	xor h			;fe93	ac 	. 
	ld (hl),e			;fe94	73 	s 
	call pe,sub_ec50h+1		;fe95	ec 51 ec 	. Q . 
	ld d,e			;fe98	53 	S 
	call pe,sub_ec53h		;fe99	ec 53 ec 	. S . 
	ld d,e			;fe9c	53 	S 
	call pe,00053h		;fe9d	ec 53 00 	. S . 
	ld d,e			;fea0	53 	S 
	nop			;fea1	00 	. 
	inc de			;fea2	13 	. 
	call pe,sub_ec73h		;fea3	ec 73 ec 	. s . 
	inc sp			;fea6	33 	3 
	call pe,0ac53h		;fea7	ec 53 ac 	. S . 
	ld de,053ech		;feaa	11 ec 53 	. . S 
	call pe,sub_ec31h		;fead	ec 31 ec 	. 1 . 
	ld (hl),e			;feb0	73 	s 
	xor h			;feb1	ac 	. 
	ld de,053ech		;feb2	11 ec 53 	. . S 
	call pe,sub_ec53h		;feb5	ec 53 ec 	. S . 
	inc de			;feb8	13 	. 
	call pe,sub_ec31h		;feb9	ec 31 ec 	. 1 . 
	ld d,c			;febc	51 	Q 
	call pe,08c11h		;febd	ec 11 8c 	. . . 
	inc de			;fec0	13 	. 
	adc a,h			;fec1	8c 	. 
	inc sp			;fec2	33 	3 
	xor h			;fec3	ac 	. 
	inc sp			;fec4	33 	3 
	call pe,sub_ec50h+1		;fec5	ec 51 ec 	. Q . 
	inc de			;fec8	13 	. 
	call pe,sub_ec11h		;fec9	ec 11 ec 	. . . 
lfecch:
	ld d,e			;fecc	53 	S 
	call pe,sub_ec11h		;fecd	ec 11 ec 	. . . 
	ld d,c			;fed0	51 	Q 
lfed1h:
	dw					0x71ec
	db					0xec
;	call pe,0ec71h		;fed1	ec 71 ec 	. q . 
	ld d,e			;fed4	53 	S 
lfed5h:
	call pe,0ac13h		;fed5	ec 13 ac 	. . . 
	ld d,c			;fed8	51 	Q 
	call pe,sub_ec11h		;fed9	ec 11 ec 	. . . 
	ld d,e			;fedc	53 	S 
	call pe,00011h		;fedd	ec 11 00 	. . . 
	ld d,e			;fee0	53 	S 
	nop			;fee1	00 	. 
	ld d,e			;fee2	53 	S 
	call pe,sub_ec73h		;fee3	ec 73 ec 	. s . 
	ld de,053ech		;fee6	11 ec 53 	. . S 
	call pe,sub_ec11h		;fee9	ec 11 ec 	. . . 
	inc sp			;feec	33 	3 
	call pe,0ac11h		;feed	ec 11 ac 	. . . 
	ld d,e			;fef0	53 	S 
	call pe,0ac13h		;fef1	ec 13 ac 	. . . 
	inc de			;fef4	13 	. 
	xor 053h		;fef5	ee 53 	. S 
	xor h			;fef7	ac 	. 
	ld de,053ech		;fef8	11 ec 53 	. . S 
	xor h			;fefb	ac 	. 
	ld de,011ech		;fefc	11 ec 11 	. . . 
	adc a,b			;feff	88 	. 
lff00h:
	dec sp			;ff00	3b 	; 
	ex af,af'			;ff01	08 	. 
	inc sp			;ff02	33 	3 
	xor h			;ff03	ac 	. 
	inc de			;ff04	13 	. 
	xor h			;ff05	ac 	. 
	inc sp			;ff06	33 	3 
	xor h			;ff07	ac 	. 
	inc sp			;ff08	33 	3 
	xor h			;ff09	ac 	. 
	ld sp,033ach		;ff0a	31 ac 33 	1 . 3 
	xor h			;ff0d	ac 	. 
	inc sp			;ff0e	33 	3 
	xor h			;ff0f	ac 	. 
	inc sp			;ff10	33 	3 
	xor h			;ff11	ac 	. 
	inc sp			;ff12	33 	3 
	xor h			;ff13	ac 	. 
	inc sp			;ff14	33 	3 
	xor h			;ff15	ac 	. 
	inc sp			;ff16	33 	3 
	xor h			;ff17	ac 	. 
	inc sp			;ff18	33 	3 
	xor h			;ff19	ac 	. 
	inc sp			;ff1a	33 	3 
	xor h			;ff1b	ac 	. 
	inc sp			;ff1c	33 	3 
	call pe,00033h		;ff1d	ec 33 00 	. 3 . 
	ld (hl),e			;ff20	73 	s 
	nop			;ff21	00 	. 
	ld sp,033ach		;ff22	31 ac 33 	1 . 3 
	xor h			;ff25	ac 	. 
	inc sp			;ff26	33 	3 
	xor h			;ff27	ac 	. 
	inc sp			;ff28	33 	3 
	xor h			;ff29	ac 	. 
	inc sp			;ff2a	33 	3 
	xor h			;ff2b	ac 	. 
	inc sp			;ff2c	33 	3 
	xor h			;ff2d	ac 	. 
	inc sp			;ff2e	33 	3 
	xor h			;ff2f	ac 	. 
	inc sp			;ff30	33 	3 
	xor h			;ff31	ac 	. 
	inc sp			;ff32	33 	3 
	xor h			;ff33	ac 	. 
	inc sp			;ff34	33 	3 
	xor h			;ff35	ac 	. 
	inc de			;ff36	13 	. 
	xor h			;ff37	ac 	. 
	inc de			;ff38	13 	. 
	xor h			;ff39	ac 	. 
	inc sp			;ff3a	33 	3 
	xor h			;ff3b	ac 	. 
	inc de			;ff3c	13 	. 
	xor h			;ff3d	ac 	. 
	jr nc,lfecch		;ff3e	30 8c 	0 . 
	inc sp			;ff40	33 	3 
	xor h			;ff41	ac 	. 
	ld (033ach),a		;ff42	32 ac 33 	2 . 3 
	xor h			;ff45	ac 	. 
	inc sp			;ff46	33 	3 
	xor h			;ff47	ac 	. 
	inc sp			;ff48	33 	3 
	xor h			;ff49	ac 	. 
	inc sp			;ff4a	33 	3 
	xor h			;ff4b	ac 	. 
	inc sp			;ff4c	33 	3 
	xor h			;ff4d	ac 	. 
	ld sp,013ech		;ff4e	31 ec 13 	1 . . 
	xor h			;ff51	ac 	. 
	inc sp			;ff52	33 	3 
	xor h			;ff53	ac 	. 
	inc sp			;ff54	33 	3 
	xor h			;ff55	ac 	. 
	inc sp			;ff56	33 	3 
	call pe,0ac33h		;ff57	ec 33 ac 	. 3 . 
	ld sp,033ach		;ff5a	31 ac 33 	1 . 3 
	xor h			;ff5d	ac 	. 
	ld sp,03300h		;ff5e	31 00 33 	1 . 3 
	nop			;ff61	00 	. 
	inc sp			;ff62	33 	3 
	xor h			;ff63	ac 	. 
	inc de			;ff64	13 	. 
	xor h			;ff65	ac 	. 
	ld sp,031ach		;ff66	31 ac 31 	1 . 1 
	call pe,0ac31h		;ff69	ec 31 ac 	. 1 . 
	ld (hl),e			;ff6c	73 	s 
	call pe,0ac33h		;ff6d	ec 33 ac 	. 3 . 
	inc sp			;ff70	33 	3 
	xor h			;ff71	ac 	. 
	inc sp			;ff72	33 	3 
	xor h			;ff73	ac 	. 
	inc sp			;ff74	33 	3 
	call pe,0ac31h		;ff75	ec 31 ac 	. 1 . 
	inc sp			;ff78	33 	3 
	xor h			;ff79	ac 	. 
	inc sp			;ff7a	33 	3 
	xor h			;ff7b	ac 	. 
	inc sp			;ff7c	33 	3 
	xor h			;ff7d	ac 	. 
	ld de,05208h		;ff7e	11 08 52 	. . R 
	nop			;ff81	00 	. 
	ld de,Cartridge_ROM		;ff82	11 00 10 	. . . 
	nop			;ff85	00 	. 
	djnz lff88h		;ff86	10 00 	. . 
lff88h:
	ld d,e			;ff88	53 	S 
	nop			;ff89	00 	. 
	djnz lff8ch		;ff8a	10 00 	. . 
lff8ch:
	ld d,e			;ff8c	53 	S 
	nop			;ff8d	00 	. 
	ld de,05100h		;ff8e	11 00 51 	. . Q 
	nop			;ff91	00 	. 
	djnz lff94h		;ff92	10 00 	. . 
lff94h:
	ld de,01200h		;ff94	11 00 12 	. . . 
	nop			;ff97	00 	. 
	inc de			;ff98	13 	. 
	nop			;ff99	00 	. 
	djnz lff9ch		;ff9a	10 00 	. . 
lff9ch:
	djnz lff9eh		;ff9c	10 00 	. . 
lff9eh:
	ld (de),a			;ff9e	12 	. 
	nop			;ff9f	00 	. 
	djnz lffa2h		;ffa0	10 00 	. . 
lffa2h:
	djnz lffa4h		;ffa2	10 00 	. . 
lffa4h:
	ld d,e			;ffa4	53 	S 
	nop			;ffa5	00 	. 
	djnz lffa8h		;ffa6	10 00 	. . 
lffa8h:
	ld de,01100h		;ffa8	11 00 11 	. . . 
	nop			;ffab	00 	. 
	ld de,Cartridge_ROM		;ffac	11 00 10 	. . . 
	nop			;ffaf	00 	. 
	ld d,c			;ffb0	51 	Q 
	nop			;ffb1	00 	. 
	djnz lffb4h		;ffb2	10 00 	. . 
lffb4h:
	ld d,b			;ffb4	50 	P 
	nop			;ffb5	00 	. 
	ld (de),a			;ffb6	12 	. 
	nop			;ffb7	00 	. 
	ld de,05100h		;ffb8	11 00 51 	. . Q 
	nop			;ffbb	00 	. 
	djnz lffbeh		;ffbc	10 00 	. . 
lffbeh:
	djnz lffc0h		;ffbe	10 00 	. . 
lffc0h:
	ld de,VIDEO_ram		;ffc0	11 00 50 	. . P 
	nop			;ffc3	00 	. 
	ld de,Cartridge_ROM		;ffc4	11 00 10 	. . . 
	nop			;ffc7	00 	. 
	djnz lffcah		;ffc8	10 00 	. . 
lffcah:
	djnz lffcch		;ffca	10 00 	. . 
lffcch:
	ld d,b			;ffcc	50 	P 
	nop			;ffcd	00 	. 
	djnz lffd0h		;ffce	10 00 	. . 
lffd0h:
	ld d,b			;ffd0	50 	P 
	nop			;ffd1	00 	. 
	djnz lffd4h		;ffd2	10 00 	. . 
lffd4h:
	ld de,Cartridge_ROM		;ffd4	11 00 10 	. . . 
	nop			;ffd7	00 	. 
	ld d,c			;ffd8	51 	Q 
	nop			;ffd9	00 	. 
	djnz lffdch		;ffda	10 00 	. . 
lffdch:
	djnz lffdeh		;ffdc	10 00 	. . 
lffdeh:
	djnz lffe0h		;ffde	10 00 	. . 
lffe0h:
	ld de,Cartridge_ROM		;ffe0	11 00 10 	. . . 
	nop			;ffe3	00 	. 
	ld de,Cartridge_ROM		;ffe4	11 00 10 	. . . 
	nop			;ffe7	00 	. 
	ld de,Cartridge_ROM		;ffe8	11 00 10 	. . . 
	nop			;ffeb	00 	. 
	ld de,Cartridge_ROM		;ffec	11 00 10 	. . . 
	nop			;ffef	00 	. 
	ld de,01100h		;fff0	11 00 11 	. . . 
	nop			;fff3	00 	. 
	djnz lfff6h		;fff4	10 00 	. . 
lfff6h:
	djnz lfff8h		;fff6	10 00 	. . 
lfff8h:
	ld de,Cartridge_ROM		;fff8	11 00 10 	. . . 
	nop			;fffb	00 	. 
	djnz lfffeh		;fffc	10 00 	. . 
lfffeh:
	djnz 0x0000		;fffe	10 00 	. . 
