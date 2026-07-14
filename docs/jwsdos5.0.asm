;
; z80dasm 1.1.5
; command line used to generate initial disassembly:
; z80dasm --origin=0xe000 --labels --output=jwsdos.asm --sym-output=jwsdos.sym --source JWS.bin
;
; make sure that
; z80asm jwsdos5.0.asm -Lthelabels.sym && diff a.bin JWS.bin && echo "all good"
; prints "all good"
;
DOS_P2000M_tab_size:        equ 056h            ; 86 TODO: (tabs 'off'?)

P2000_modelM_attributes:    equ 05800h

P2000_cursor_type:          equ 06013h          ; contains 0 for P2000T (cursor is inverted character), 1 for P2000M (effectively hides cursor)
MON_interrupt_ch0:          equ 06020h          ; disk action completed / CTC
MON_interrupt_ch1:          equ 06022h          ; disk not ready

DE_size:                    equ 32              ; size of directory entry on disk/in memory
; 'active' directory entry in memory
DE_current_header:          equ 06030h          ; active dir entry
DE_filename:                equ 06030h          ; filename in entry
DE_extension:               equ 06040h          ; extension
DE_filetype:                equ 06043h
DE_filelen:                 equ 06044h          ; in bytes
DE_transfer:                equ 06046h          ; load/save address
DE_head:                    equ 06048h          ; disk side
DE_start_sector:            equ 06049h          ; sectors are linearly numbered 1-16 = track 1, 17-32 tr 2 etc
DE_end_sector:              equ 0604bh
DE_filelen_LO:              equ DE_filelen
DE_filelen_HI:              equ DE_filelen+1
DE_sec_trk:                 equ 0604dh          ; used to access sec/track at once
DE_sec:                     equ 0604dh          ; to access sector separately
DE_trk:                     equ 0604eh          ; to access track separately
DE_sec_count:               equ 0604fh          ; # of sectors of file

BASIC_Memory_Size:          equ 0605ch
savedstack:                 equ 0608eh
bas_key_map_table:          equ 06094h          ; pointer to key-translation map of BASIC
BASIC_Cassette_flags:       equ 060ach          ; bit 0 set (01h): don't rewind for next cassette operation
                                                ; bits 6 and 4 set (50h): ask for 'overwrite' while RUNNING
                                                ; bit 7 set (80h): ask nothing during CSAVE
BASIC_csave_vector:         equ 060cdh
BASIC_command_hook:         equ 060d0h          ; Jump address called BEFORE any BASIC instruction
BASIC_string_save:          equ 060ddh
BASIC_keyparse_hook:        equ 060e9h
MON_transfer_address:       equ 06130h
MON_file_length:            equ 06132h
BASIC_real_file_len:        equ 06134h          ; sometimes data len != payload len. this is the payload len
MON_file_name_part1:        equ 06136h          ; first 8 chars of filename
MON_file_extension:         equ 0613eh          ; 3 characters extension
BASIC_binary_start:         equ 06143h
MON_file_name_part2:        equ 06147h          ; last  8 chars of filename

BASIC_temp_storage:         equ 06150h          ; free to use as temp memory
BASIC_top_of_temp_storage:  equ 061ffh          ; free to use as temp memory
BASIC_return_to_basic:      equ 06200h          ; clean return to basic
BASIC_Usr_vectors:          equ 0623eh          ; Table of 10 vector words pointing to Usr() code
BASIC_error_code:           equ 06252h
BASIC_tab_spacing:          equ 06256h          ; normally 14 (0x0E)
BASIC_Start_Stringspace:    equ 06258h
BASIC_active_program_line:  equ 0625ah
BASIC_start_of_prog:        equ 0625ch
BASIC_input_buffer:         equ 06260h          ; 256 bytes buffer for input of basic. first 2 bytes zero means not in use.
DOS_src_drv:                equ 06360h          ; for copy action
DOS_dst_drv:                equ 06361h
DOS_last_sector_flag:       equ 06362h
ramdisk_trackcount:         equ 06363h
DOS_only_full_sectors:      equ 06364h
ramdisk_status:             equ 06365h
string_save_Backup:         equ 06367h
csave_address_backup:       equ 06369h
DOS_save_input_ptr:         equ 0636bh
DOS_next_input_ptr:         equ 0636dh
DOS_used_sectors:           equ 0636fh
DOS_basic_stack:            equ 06372h
BASIC_Top_of_Mem:           equ 063b8h
BASIC_first_free_string:    equ 063ddh          ; BASIC_first_free_string
BASIC_start_minus_one:      equ 063ebh
BASIC_input_ptr:            equ 063f2h
BASIC_on_error_dest:        equ 063fch
BASIC_on_error_active:      equ 063feh
BASIC_byte_before_error:    equ 06403h
BASIC_start_of_vars:        equ 06405h
BASIC_start_of_arrays:      equ 06407h
BASIC_end_of_arrays:        equ 06409h

BAS_CHAR_OUT:               equ 0104ah
BASIC_FAST_LIST_MODE:       equ 011e6h
BASIC_print_part:           equ 011ech          ; print part ACTION: validate in BASIC binary
BASIC_txt_Zoek:             equ 01488h
BASIC_txt_Niet_Gevonden:    equ 014c3h
BASIC_print_int:            equ 01601h          ; print int in HL right-aligned to screen ACTION: validate in BASIC binary
BASIC_print_aligned:        equ 0162bh          ; ACTION: validate in BASIC binary
BASIC_csave_entry:          equ 016f0h          ; entry of CSAVE code in BASIC
BASIC_input:                equ 01911h          ; returns C if 'Stop' was pressed
BASIC_check_Stop:           equ 01918h          ; terminates when Stop is pressed, returns if not
BASIC_char_out_aligned:     equ 019a9h          ; ACTION: validate in BASIC binary
BASIC_print_CRLF:           equ 01a2ah
BAS_CURSOR_ON:              equ 01addh
BAS_CURSOR_OFF:             equ 01ae1h
BASIC_map_keys:             equ 01c48h          ; HL points to translate table, A contains key
                                                ; returns NZ and translated value in A on success
BASIC_compare_HL_DE:        equ 01cfdh          ; Compare HL and DE: Z if HL==DE, S&C when HL<DE, NZ&P&NC when HL>DE
BASIC_CMD_RESTORE:          equ 01d16h
BASIC_txt_bytes_vrij:       equ 01fcfh          ; yellow,'bytes vrij', goto line7, pos 1
BASIC_txt_out_of_memory:    equ 0229fh
BASIC_Errormessage:         equ 0247dh          ; errorcode in E
BASIC_update_line_pointers: equ 025fbh          ; needed after loading basic program
BASIC_get_loadsave_parms:   equ 02651h          ; tokenize basic input
BASIC_get_next_ch:          equ 02833h          ; carry set when next char is a digit
BASIC_get_current_ch:       equ 02834h          ; carry set when next char is a digit
BASIC_Print_text:           equ 03383h          ; prints zero terminated string to screen
BASIC_csave:                equ 04e5dh          ; entry for CSAVE in Basic


BASIC_input_char:           equ 0104dh          ; read a char from keyboard
BASIC_txt_Ja:               equ 01450h          ; pointer to "Ja"
BASIC_txt_Overheen:         equ 01454h
BASIC_txt_Nee:              equ 014e0h          ; pointer to "Nee"
BASIC_mem_copy:             equ 01c5ah          ; copy memory
BASIC_txt_ja_nee:           equ 01d0eh          ; text "? (J/N)"
BASIC_RUN:                  equ 028d4h          ; run program
BASIC_PARSE_EXPR_TO_INT:    equ 032e4h          ; returns result in DE

BASIC_CALC_EXPRESSION:      equ 03303h          ; Calc expression from HL. result in A and DE,  if >256: syntax error
BASIC_print_hl_decimal:     equ 03e9ah
BASIC_print_text_2:         equ 04a81h
BASIC_find_room_for_string: equ 04a9ah
BASIC_no_room_for_string:   equ 04ab9h
BASIC_garbage_collect:      equ 04ac5h
BASIC_CMD_DATA:             equ 0293ah          ; parses input and ignores / skips constants and vars

DIR_side_1_mem:             equ 0f000h
DIR_side_2_mem:             equ 0f800h

FDC_mode_write:             equ 045h
FDC_mode_read:              equ 046h
STOP_key:                   equ 003h

ramdisk_tmp_storage:        equ 0f165h

MON_DSK_read_IO_status:     equ 00f62h
MON_DSK_gotrack:            equ 00f7dh
MON_DSK_init:               equ 00ee2h
MON_DSK_motor_on:           equ 00f88h
MON_DSK_delay_342ms:        equ 00effh
MON_DSK_send_command:       equ 00fa5h
MON_DSK_read_status_bytes:  equ 00f90h
MON_DSK_calibrate:          equ 00f08h

MON_working_mem:            equ 06087h          ; area of 7 bytes
MON_dummy_handler:          equ 00fd6h          ; just 'ei reti' instructions
MON_write_sector:           equ 0047dh
MON_read_sector:            equ 0046ah

dsk_transfer_adr:           equ 06070h
dsk_transfer_cmd:           equ 06072h
dsk_transfer_cmd_IOtype:    equ 06073h          ; 42h = read, 45h = write
dsk_transfer_cmd_drive:     equ 06074h
dsk_transfer_cmd_trk:       equ 06075h
dsk_transfer_cmd_head:      equ 06076h
dsk_transfer_cmd_sec:       equ 06077h
dsk_seek_cmd_drive:         equ 0607eh
dsk_recall_cmd_drive:       equ 06082h

JWS_ERR_NOT_FOUND:          equ 020h
JWS_ERR_WRITE_PROTECTED:    equ 021h
JWS_ERR_DISK_FAILURE:       equ 022h
JWS_ERR_ILLEGAL_DRIVE:      equ 023h
JWS_ERR_DISK_FULL:          equ 024h
JWS_ERR_OUT_OF_MEMORY:      equ 025h

BASIC_ERR_CASS_A:           equ 'A'
BASIC_ERR_CASS_E:           equ 'E'
BASIC_ERR_CASS_G:           equ 'G'
BASIC_ERR_CASS_M:           equ 'M'
BASIC_ERR_OUT_OF_MEMORY:    equ 7
BASIC_ERR_SYNTAX:           equ 2

chr_GOTO_XY:                equ 004h
chr_CLS:                    equ 00ch
chr_CLEAR_LINE:             equ 00fh
chr_CRLF:                   equ 01dh
chr_GREEN:                  equ 082h
chr_YELLOW:                 equ 083h
chr_BLUE:                   equ 084h
chr_PURPLE:                 equ 085h
chr_CYAN:                   equ 086h
chr_HEIGHT_NORMAL:          equ 08ch
chr_HEIGHT_DOUBLE:          equ 08dh

RAW_KEY_DEF:                equ 05bh
RAW_KEY_M_shft9:            equ 078h
RAW_KEY_DISK:               equ 07ah
RAW_KEY_START:              equ 080h
RAW_KEY_LIGHTNING:          equ 082h
RAW_KEY_ZOEK:               equ 083h
RAW_KEY_OPN:                equ 088h
RAW_KEY_shft5:              equ 08ah
RAW_KEY_INL:                equ 08bh

JWS_CMD_DIR:                equ 080h
JWS_CMD_TAPE:               equ 081h
JWS_CMD_AUTORUN:            equ 082h
JWS_CMD_RUN:                equ 083h
JWS_CMD_DEFRAG:             equ 085h
JWS_CMD_numpad_M:           equ 08ah
JWS_CMD_COPY:               equ 08bh
JWS_CMD_SAVE:               equ 08ch
JWS_CMD_LOAD:               equ 08dh
JWS_CMD_DISK:               equ 08eh

CTC_CHANNEL0:               equ 088h
CTC_CHANNEL1:               equ 089h
CTC_CHANNEL2:               equ 08ah
CTC_CHANNEL3:               equ 08bh

FDC_STATUS:                 equ 08dh
FDC_CONTROL:                equ 090h

ramdisk_Track:              equ 095h
ramdisk_Sector:             equ 096h
ramdisk_IO:                 equ 097h


    org 0e000h

    jr nz,le002h                        ; TODO: function of this?
le002h:
    jp StartDOS                         ; Start of program
return_to_basic:
    jp BASIC_return_to_basic            ; Clean exit to basic
jump_to_basic_error:
    jp BASIC_Errormessage               ; handle BASIC error

    jp print_file_info                  ; I guess addresses usable from bassic/ml
    jp disk_list_dir                    ; to allow user-access to dos-functionality
    jp perform_read                     ;
    jp perform_write                    ;

dos_hook_active:
    db  00h                             ; flag: 1 = JWS dos hook active, 0 = inactive

    jp handle_API_call                  ; Best guess: debug/library entry

disk_do_IO:
    jp do_disk_IO
disks_on:
    jp start_disks
disks_off:
    call stop_disks
    ret

    db  0b1h,04fh                       ; Dead code/data?

go_track:
    jp goto_track
exit_if_write_protect:
    jp is_write_enabled
get_disk_status:
    jp read_disk_IO_status

exit_with_basic_error:
    ld a,e                              ; copy errorcode in A
    ld hl,dos_to_basic_error_map
    call BASIC_map_keys                 ; try to map the error
    ld sp,(savedstack)                  ; restore stack
    jp jump_to_basic_error              ; and hand over to basic error handler

dos_to_basic_error_map:
    db  6                                                   ; 6 items are mapped
    db  JWS_ERR_DISK_FULL,          BASIC_ERR_CASS_E        ; cass full
    db  JWS_ERR_NOT_FOUND,          BASIC_ERR_CASS_M        ; cass 'not found'
    db  JWS_ERR_DISK_FAILURE,       BASIC_ERR_CASS_A        ; cass 'no tape'
    db  JWS_ERR_WRITE_PROTECTED,    BASIC_ERR_CASS_G        ; cass 'no write enable plug'
    db  JWS_ERR_OUT_OF_MEMORY,      BASIC_ERR_OUT_OF_MEMORY ; out of memory
    db  JWS_ERR_ILLEGAL_DRIVE,      BASIC_ERR_CASS_A        ; cass 'no tape'

StartDOS:
    ld (savedstack),sp                  ; save stackpointer for clean exit
    ld a,(dos_hook_active)              ; is dos-hook
    and a                               ; activated?
    jp z,insert_dos_hook                ; No, so place hook and exit

print_drive_info:
    call BAS_CURSOR_OFF                 ; turn cursor off
    ld hl,txt_DS_80Tr_drive             ; display selected drive info
    call BASIC_Print_text
    call fdc_drive_to_dos_drive         ; get drive #
    and a                               ; zero?
    jr nz,di_not_drive0                 ; no
    set 2,a                             ; 0 becomes 4
di_not_drive0:
    add a,'0'                           ; turn into printable digit
    call BAS_CHAR_OUT                   ; and print
    call BASIC_print_CRLF
    call BAS_CURSOR_ON
    call BASIC_input_char
;
; this key only works immediately after pressing 'DISK'
;

    cp JWS_CMD_COPY                     ; 'lightning' SHIFT numpad 2
    call z,disk_copy_disk
dos_key_parser:
    cp JWS_CMD_SAVE                     ; OPN
    call z,disk_save_file
    cp JWS_CMD_LOAD                     ; INL
    call z,disk_load_file
    cp chr_CLEAR_LINE                   ; Wis Regel (Erase line)
    call z,disk_erase_file
    cp chr_CLS                          ; Wis Scherm (clear screen)
    call z,disk_erase_directory
    cp JWS_CMD_AUTORUN                  ; DEF
    call z,disk_autorun
    cp JWS_CMD_DIR                      ; 'ZOEK' (search)
    call z,disk_list_dir
    cp JWS_CMD_COPY                     ; 'lightning' SHIFT numpad 2
    call z,disk_copy_file
    cp JWS_CMD_RUN                      ; START
    call z,disk_run_file
    cp JWS_CMD_DEFRAG                   ; SH 5
    call z,disk_defragment
    cp 010h                             ; left arrow
    jr z,decrease_tracks
    cp 013h                             ; right arrow?
    jr z,increase_tracks
    call char_between_1_4               ; '1' to '4' ?
    jr nc,select_drive                  ; yes
    res 5,a                             ; remove bit 5 (make upper-case)
    cp 'S'
    jr z,set_SS_DS
    cp 'D'
    jr z,set_SS_DS
    cp 'R'
    jp z,toggle_ramdisk
; no valid key, return to basic
    ld sp,(savedstack)
    jp return_to_basic

select_drive:
    and 00fh                            ; remove unnecessary bits
    ld (system_drive),a                 ; and store

update_drive_info:
    ld a,011h                           ; print up-arrow moves cursor 1 line up
    call BAS_CHAR_OUT
    jp print_drive_info                 ; print updated drive info

set_SS_DS:
    ld (SS_DS_Char),a                   ; set SS/DS character
    jr update_drive_info
;
; set track info
; HL points to 3 bytes:
; 1st byte: # of tracks + 1 (36, 41 or 81): for internal use
; 2nd and 3rd bytes '35', '40' or '80': text to display
;
set_track_info:
    ld a,(hl)                           ; Store number of tracks
    ld (number_of_tracks),a             ; for internal use
    ld de,track_count_chars
    inc hl                              ; point to 1st char of trackstring
    ld a,(hl)                           ; store in trackinfo string
    ld (de),a
    inc hl                              ; 2nd char of trackstring
    inc de
    ld a,(hl)                           ; and store
    ld (de),a
    jr update_drive_info
decrease_tracks:
    ld a,40+1                           ; compare internal number of tracks
    ld hl,number_of_tracks              ; with 41
    cp (hl)
    jr c,set_40_tr                      ; if C current is 81, make 40
    jr z,set_35_tr                      ; if Z current is 41, make 35
set_80_tr:
    ld hl,trackinfo_80                  ; make 80
    jr set_track_info

set_40_tr:
    ld hl,trackinfo_40
    jr set_track_info

set_35_tr:
    ld hl,trackinfo_35
    jr set_track_info                   ; NOTE: jr can be eliminated by moving the code here...

increase_tracks:
    ld a,40+1                           ; compare internal number of tracks
    ld hl,number_of_tracks              ; with 41
    cp (hl)
    jr c,set_35_tr                      ; if C: current is 81, make 35
    jr z,set_80_tr                      ; if Z: current is 41, make 80
    jr set_40_tr                        ; make 40

trackinfo_35:
    db          35+1,"35"
trackinfo_40:
    db          40+1,"40"
trackinfo_80:
    db          80+1,"80"
;
; is input in range 1..4?
; returns:
; C if out of range
; NC if valid
;
char_between_1_4:
    cp '1'                              ; if char < '1'
    ret c                               ; then it is invalid
    cp '5'                              ; if < '5' then carry is set
    ccf                                 ; invert because <'5' is okay, and >='5' not.
    ret

ext_OBJ:
    db          'OBJ'
    db          019h                    ; dummy byte, not used
set_extension_OBJ:
    ld hl,ext_OBJ
    jp set_extension

; HL contains pointer to input buffer
handle_params:
    call next_char_valid                ; read start address
    db  ','
    call BASIC_PARSE_EXPR_TO_INT        ; result in DE
    ld (MON_transfer_address),de        ; save in system file info
    push de                             ; save start address for length calculation
    call next_char_valid                ; get end address
    db  ','
    call BASIC_PARSE_EXPR_TO_INT        ; result in DE
    ex de,hl                            ; move to HL
    pop de                              ; get start address in DE
    xor a                               ; clear carry
    sbc hl,de                           ; calculate length
    inc hl                              ; add one because end address is included: save "ddd",1,1 must save 1 byte
    ld (MON_file_length),hl             ; store as length
    ld de,07001h                        ; Max file length
    sbc hl,de                           ; compare
    ld hl,02260h                        ; ACTION? 02260h (in BASIC) is in the middle of the message "RETURN without GOSUB"
    ld e,006h                           ; ACTION? Error code = Overflow?
    jp nc,rest_ints_and_error           ; if size >= 7001h then too big ERROR!!!

    call set_extension_OBJ
    call mon_file_len_adr_to_dir
    jr save_check_existing

disk_save_file:
    ld hl,txt_save_quotes               ; 'Save"'  to screen
    call get_filename_and_header
    ret c                               ; exit when 'Stop' pressed

    push hl                             ; save pointer to input buffer
    call read_directory
    call mon_file_to_dir_file           ; convert system file info to JWSdos dir entry info
    pop hl                              ; input buffer
    ld a,(hl)                           ; is there more input after the filename?
    and a
    jr nz,handle_params                 ; yes, get start and length from input buffer

    call set_file_length                ; get start address and length info from system
save_check_existing:
    call find_file_in_dir
    jp c,ask_overwrite_file             ; carry set: file exists

save_file_and_dir:
    call write_file
    call save_directory
    jp disks_off

set_file_length:
    ld hl,(BASIC_start_of_vars)         ; start of variable space, also end of basic program
    ld de,(BASIC_start_of_prog)         ; set transfer to start of basic (usually 6547h)
    ld (DE_transfer),de
    xor a                               ; clear carry
    sbc hl,de                           ; calculate file length (end-start)
    ld (DE_filelen),hl                  ; and save in dir entry
    ret

write_file:
    call exit_if_write_protect          ; returns when write enabled
    call find_room                      ; searches fitting gap between files or at end
; after call to find room, header contains starting sector and head
; DE : last sector#+1 of preceding file;
; BC : length of file in sectors
; IY : points to directory entry to insert BEFORE
    ld hl,(DE_start_sector)             ; get startsector #
    ld a,h
    or l
    jp z,disk_full_error                ; if startsector == 0 disk full. Error!
    dec de                              ; startsector -1
    push de                             ; copy to HL
    pop hl
    add hl,bc                           ; add # of sectors
    ld (DE_end_sector),hl               ; save in header
    ld a,'B'                            ; indicate type is Basic file
    ld (DE_filetype),a
    call insert_dir_entry
    jp disk_write_action                ; save the file and return

;
; entry point/API for other programs?
; method/dos command to execute in A
; 1 = read dir
; 4 = disks off
; 5 = save file
; 6 = load file
; 7 = not implemented
; 8 = erase all files
; 9 = delete file
;
handle_API_call:
    ld (savedstack),sp                  ; save basic stackpointer
    cp 1                                ;
    ld de,MON_transfer_address          ; transfer address set?
    call z,copy_active_header           ; 1 = fill header
    jp z,read_directory                 ; load dir and exit

    cp 4
    jp z,clear_FDC_and_interrupts       ; 4 = stop disks
    cp 5
    jp z,save_file                      ; save file, ask for overwrite etc.
    cp 6
    jp z,load_file
    cp 7
    jr z,no_action                      ; not implemented
    cp 8
    jr z,erase_directory_noask
    cp 9
    jp z,del_file
    cp 4                                ; 4 or higher?
    ret nc                              ; then done
no_action:
    xor a                               ; key is handled
    ret

disk_erase_directory:
    ld hl,txt_disk_wissen
    call prt_txt_ask_Ja_Nee             ; terminates on No/Nee
erase_directory_noask:
    xor a                               ; fill 4Kb dir area with zeros
    ld hl,DIR_side_1_mem
    ld (hl),a
    push hl
    pop de
    inc de
    ld bc,00fffh
    ldir
    call disks_on                       ; and try to save
    call exit_if_write_protect
    call save_directory
    jp disks_off

parse_load_parameters:
    call next_char_valid                ; parse load address
    db  ','
    call BASIC_PARSE_EXPR_TO_INT        ; result in DE
    push de                             ; save address
    call set_extension_OBJ              ; override suppplied extension; only OBJ can be relocated
    call search_file
    pop de                              ; set correct load address
    ld (DE_transfer),de
    call calc_end_address
    jr load_file_data_if_fits           ; loads only when file fits

disk_load_file:
    ld hl,txt_load_quotes               ; 'load"' to screen

get_name_and_load:
    call get_filename_and_header
    ret c                               ; return when 'Stop' was pressed
    push hl                             ; save pointer to next char in input buffer
    call read_directory                 ; get directory
    call mon_file_to_dir_file           ; prep file info
    pop hl                              ; get pointer to input
    ld a,(hl)
    and a
    jr nz,parse_load_parameters         ; more after Load"name" process it!
;
; exits with error if not found
;
find_file_and_load:
    call search_file                    ; error and exit if not found

load_found_file:
    ld hl,(BASIC_start_of_prog)         ; set load address
    ld (DE_transfer),hl
    call load_if_file_fits              ; exits if not
    ld de,(DE_transfer)                 ; get start-address
    push de
    call BASIC_update_line_pointers
    pop de                              ; calculate end address of BASIC
    ld hl,(DE_filelen)
    add hl,de
set_end_of_basic_prog:
    ld (BASIC_start_of_vars),hl         ; VARS start after BASIC program data
    ld (BASIC_start_of_arrays),hl       ; set Start of arrays
    ld (BASIC_end_of_arrays),hl         ; set End of arrays (empty!)
    ret
;
; exits with error if not found
;
search_file:
    call find_file_in_dir
    jr nc,not_found

    push ix                             ; get info from found file
    pop hl
    jp to_current_header

load_if_file_fits:
    call calc_end_address               ; Does the file fit?
    ex de,hl                            ; move address to DE
    ld hl,(BASIC_Start_Stringspace)     ; Get highest possible address
    xor a                               ; and compare
    sbc hl,de

load_file_data_if_fits:
    jp c,file_too_large_error           ; C set means too large!
read_data_and_disks_off:
    call disk_read_action
turn_off_disks:
    jp disks_off

; returns end address of file in HL
calc_end_address:
    ld hl,00032h                        ; take string area (50) bytes in account
    ld de,(DE_transfer)                 ; add to start address
    add hl,de
    ld de,(DE_filelen)                  ; add length to get end-address
    add hl,de
    ret

perform_read:
    call disks_on
    jr read_data_and_disks_off

perform_write:
    call disks_on
    call disk_write_action
    jr turn_off_disks

disk_erase_file:
    ld hl,txt_wis_quotes                ; ask for file to delete
    call get_filename_and_header
    ret c                               ; Stop was pressed; exit
erase_file:
    call read_directory
    call exit_if_write_protect
    call mon_file_to_dir_file           ; fill in header info
del_file:
    call find_file_in_dir

not_found:
    jp nc,file_not_found_error          ; NC means not found
    call remove_from_dir                ; remove deleted file from directory
    call save_directory
    jr turn_off_disks

remove_from_dir:
    push ix                             ; pointer to dir entry to delete
    ld hl,DE_size                       ; get pointer to next entry by adding size of directory entry
    pop de                              ; dir entry
    add hl,de                           ; add 32 (point hl to next entry)
    ex de,hl                            ; save from address in DE
    push ix                             ; copy start address to BC
    pop bc
    ld hl,DIR_side_2_mem                ; start of 2nd bank (and end of first+1)
    call BASIC_compare_HL_DE            ; is source address in 2nd dir bank
    jr nc,move_entries                  ; no
    ld hl,00000h                        ; end of 2nd bank + 1
move_entries:
    dec hl                              ; decrement to point to last byte to move
;
; performs a kind of LDIR/LDDR routine with
; DE source
; BC dest
; HL end address of data to move
;
move_bytes_loop:
    call BASIC_compare_HL_DE            ; Compare HL and DE: Z if HL==DE, S&C when HL<DE, NZ&P&NC when HL>DE
    ld a,(de)                           ; move byte from source to dest
    ld (bc),a
    ret z                               ; if DE == HL last byte was moved!

    inc bc                              ; next dest
    inc de                              ; next source
    jr move_bytes_loop
; REMARK Why not a simple LDIR with source in HL, Dest in DE and count in BC?
; where count = 800h - (HL & 07FFh).
; and then fill the last 20 bytes at DE with 00 to prevent duplication of last dir entry!
; Addition: found out that the last entry is never used (kept empty) simplifies it even more :-)

list_fileinfo_and_load:
    call search_file                    ; find file
    call print_fileinfo                 ; name, len etc to screen
    ld hl,06547h                        ; load from basic start
    ld (DE_transfer),hl
    call disk_read_action               ; and read
    ret

disk_defragment:
    ld hl,txt_crunch                    ;
    call prt_txt_ask_Ja_Nee             ; ask confirmnation, exits on No
    call BAS_CURSOR_OFF
    call CLEAR_dfff                     ; make all memory available
    call BASIC_FAST_LIST_MODE
    ld sp,BASIC_top_of_temp_storage     ; stack to scratch mem
    call read_directory
    ld a,001h                           ; force loading/saving of full sectors
    ld (DOS_only_full_sectors),a
    ld hl,00001h                        ; find a gap of at least 1 sector
    ld (DE_filelen),hl
    call find_room                      ;
    push iy                             ; copy pointer to entry to insert before to HL
    pop hl

crunch_next_file:
    push hl                             ; save dir entry to move
    ld a,(hl)                           ; end of dir?
    and a
    jr z,crunch_side_done               ; yes
    call to_current_header              ; file to move
    call list_fileinfo_and_load         ; display and load
    call find_file_in_dir               ; delete file and update dir
    call remove_from_dir
    call write_file                     ; save file
    ld a,(DE_head)                      ; save side
    push af
    call save_directory
    pop af                              ; side
    pop ix                              ; pointer to dir entry in IX
    push ix                             ; back on stack
    ld e,a                              ; previous side in e
    ld a,(ix+018h)                      ; get next file side
    pop hl                              ; pointer to entry in HL
    xor e                               ; side swapped?
    jr nz,crunch_next_file              ; yes, continue
    ld de,DE_size                       ; no, skip to next entry
    add hl,de
    jr crunch_next_file

crunch_side_done:
    pop de                              ; get pointer to last entry
    call is_disk_SS                     ; disk DS?
    jr z,crunch_all_done                ; no crunch is done
    ld hl,DIR_side_2_mem                ; 2nd part of directory
    ld a,d                              ; was last entry already side 2?
    bit 3,a
    jr z,crunch_next_file               ; no, crunch next side
crunch_all_done:
    jp clear_basic_and_exit
;
; copy directory entry to working area
; HL points to dir entry
; returns:
; HL points to working area
; flags: Z  if entry is empty
;        NZ if entry contains file info
;
to_current_header:
    ld de,DE_current_header             ; destination
    call copy_header                    ; copy 20 bytes from HL to DE
    ld hl,DE_current_header             ; check 1st byte for 0
    ld a,(hl)
    and a
    ret

disk_list_dir:
    call BASIC_print_part               ; ACTION: what does this exactly do, find out in BASIC binary
    call BAS_CURSOR_OFF                 ; hide cursor
list_dir_from_zoek:
    ld hl,BASIC_txt_Zoek                ; 'Zoek' to screen
    call BASIC_Print_text
    ld hl,00000h                        ; reset used sector counter
    ld (DOS_used_sectors),hl
    call read_directory                 ; load directory from disk
    call disks_off
    call is_disk_SS                     ; if SS
    jr z,list_side_1                    ; don't print 'Kant 1'
    ld hl,txt_kant                      ; print 'Kant '
    call BASIC_Print_text
    ld a,'1'                            ; print '1'
    call BAS_CHAR_OUT
    call BASIC_print_CRLF
list_side_1:
    ld hl,DIR_side_1_mem                ; start of directory for side 1
list_dir_loop:
    push hl                             ; save pointer to current dir entry
    call to_current_header              ; copy to current header area
    jr z,list_side_done                 ; Z means no more files
    call print_fileinfo                 ; display info
    ld de,DE_size                       ; skip to next dir entry (add 32 bytes)
    pop hl
    add hl,de
    jr list_dir_loop

list_side_done:
    call print_free_sectors
    pop hl                              ; get last entry address from stack
    call is_disk_SS                     ; single side?
    ret z                               ; then it's done
; perhaps simpler:
; ld a,h
; bit 3,a
; ret nz
; ld hl,txt_kant
; etc
; and no push/pop of HL and single ld HL,Disr_side_2 at the end...
    ex de,hl                            ; last address to DE
    ld hl,DIR_side_2_mem                ; side 2 directory
    ld a,d                              ; test for bit 3 in D (set when last dir entry was from side 2)
    bit 3,a
    ret nz                              ; if set: we're done
    push hl                             ; save start of DIR side 2
    ld hl,txt_kant                      ; print 'Kant '
    call BASIC_Print_text
    ld a,'2'                            ; print "2"
    call BAS_CHAR_OUT
    call BASIC_print_CRLF
    pop hl
    jr list_dir_loop
;
; prints file info to screen
; 16 bytes filename 3 bytes ext 1 byte type #sectors # of bytes
;
print_file_info:
    ld hl,DE_filename                   ; working header at 6030 starts with filename
print_fileinfo:
    ld a,chr_GREEN
    ld b,011h                           ; 17 characters filename
    call BASIC_print_aligned            ; print to screen
    ld a,chr_GREEN
    ld b,004h                           ; 4 characters extension
    call BASIC_print_aligned            ; print
    ld a,chr_CYAN
    call BASIC_char_out_aligned
    ld a,(hl)                           ; filetype (1 character)
    call BASIC_char_out_aligned         ; print
    call sectors_for_file               ; sectorcount in A
    ld l,a                              ; transfer to HL
    ld h,0
    push hl                             ; save for later
    ld de,(DOS_used_sectors)            ; add to used sector count
    add hl,de
    ld (DOS_used_sectors),hl
    pop hl                              ; # of sectors
    call BASIC_print_int                ; and print
    ld hl,BASIC_print_CRLF              ; put address of CRLF on stack
    push hl
    ld hl,(DE_filelen)                  ; print length in bytes
    call BASIC_print_int
    jp BASIC_check_Stop                 ; Test for stop key, terminates if pressed
                                        ; RETurns if not and will RET to BASIC_print_CRLF
                                        ; that was just pushed on the stack and continue there.
;
; Calculate # of free sectors and print to screen
; subtract used sectors and 32 sectors for DOS from sectors per side
; print result and adds 'vrij' or 'vol'
;
print_free_sectors:
    ld b,17                             ; print 17 spaces
    ld a,' '
spaces_loop:
    call BAS_CHAR_OUT
    djnz spaces_loop
    ld a,chr_GREEN
    call BASIC_char_out_aligned         ; set color
    call get_sectors_per_side           ; sectors on a side +1
    xor a                               ; Clears caarry for SBC
    ld de,(DOS_used_sectors)
    sbc hl,de
    ld de,16*2+1                        ; 2 tracks for dos, and correct for +1
    sbc hl,de
    push hl                             ; save # of free scectors on stack
    ld a,h                              ; 0 free?
    or l
    jr nz,not_full                      ; no
    ld hl,txt_vol
    jr print_txt_free_secs
not_full:
    ld hl,txt_vrij
print_txt_free_secs:
    call BASIC_Print_text
    ld a,chr_CYAN
    call BASIC_char_out_aligned         ; to Screen
    pop hl                              ; # of free sectors from stack
    call BASIC_print_int                ; basic print int in HL right aligned
    ld hl,00000h                        ; Reset used sector counter
    ld (DOS_used_sectors),hl            ; to zero
    ret
txt_vrij:
    db              "vrij",0
txt_vol:
    db              "vol",0

disk_run_file:
    ld hl,txt_run                       ; 'run"' to screen
    call get_name_and_load
    ret c                               ; Stop pressed: exit
    jp BASIC_RUN
;
; get filename and fill P2000 header info
; returns:
; HL = pointer to next character in input buffer
;
get_filename_and_header:
    call BASIC_Print_text               ; print prompt to screen
    ld hl,(bas_key_map_table)           ; save orignal keymap pointer on stack
    push hl
    ld hl,input_keys_off                ; turn some keys off during input
    ld (bas_key_map_table),hl
    call BASIC_input                    ; input string (without '?' prompt)
    pop hl                              ; restore original keymap pointer
    ld (bas_key_map_table),hl
    ret c                               ; stop was pressed: abort
    ld a,'"'                            ; open quotes
    ld hl,BASIC_input_buffer
    ld (hl),a                           ; in input buffer
    call prepare_fileheader
    scf                                 ; set carry flag
    ccf                                 ; invert (clear!) to indicate all is well.
    ret

prepare_fileheader:
    call BASIC_get_loadsave_parms
    push hl                             ; save pointer to next char in input buffer
    ld hl,csave_hook                    ; address of our code
    ld (BASIC_csave_vector),hl          ; CSAVE will now jump to our code
    pop hl                              ; pointer to input buffer
    inc hl                              ; next character
    call BASIC_csave                    ; Call CSAVE in BASIC, with HL pointing to parameter(s)
                                        ; in this case the filename
                                        ; This will fill in the file header at 0x6130
                                        ; then jumps through CALL 60CC to actually perform the save
                                        ; but we made it jump to our own code
                                        ; It will not do much
csave_hook:
    push hl                             ; save pointer to next character of input buffer
    ld hl,BASIC_csave_entry             ; restore CSAVE vector (Remark: is restore from backup location better?)
    ld (BASIC_csave_vector),hl
    pop hl                              ; get next input char pointer
    ret
;
; compare header with directory
; returns
; C: Found, IX points to matching DIR entry
; NC: Not found, IX rubbish
;
find_file_in_dir:
    ld ix,DIR_side_1_mem                ; start of dir, side 1
cmp_next_entry:
    push ix                             ; save pointer to current dir entry
    ld b,19                             ; check 16 + 3 = 19 characters (16name+3extension)
    ld hl,DE_filename                   ; start of active file info
cmp_char:
    ld a,(ix+000h)                      ; get 1st char
    and a                               ; zero means end of directory reached
    jr z,search_nxt_side                ; try next side

    cp (hl)                             ; compare with what we search
    jr z,nxt_char                       ; char matches, do next
    pop ix                              ; current dir entry
    ld de,DE_size                       ; skip 32 bytes to next
    add ix,de
    jr cmp_next_entry
nxt_char:
    inc ix                              ; next char
    inc hl
    ld a,(hl)                           ; get next char to search for
    cp '*'                              ; wildcard?
    jr z,file_found                     ; then it matches!
    djnz cmp_char                       ; compare next
file_found:
    scf                                 ; set carry
    pop ix                              ; matching entry in IX
    ret

search_nxt_side:
    pop ix                              ; last entry from stack
    call is_disk_SS                     ; if SS, file is not found
    jr z,file_not_found
    push ix                             ; last entry pointer
    ld ix,DIR_side_2_mem                ; start of side 2 dir data
    pop de                              ; last pointer
    ld a,d                              ; hi byte in a
    bit 3,a                             ; test bit 3
    jr z,cmp_next_entry                 ; if not set: search on on side 2
    ;  bit 3 was set, side 2 is done too: file not found!
file_not_found:
    xor a                               ; XOR A also clears carry flag
    ret
;
; Find a location where the file fits.
; for each side:
; - First try to find a fitting gap between two consecutive files.
; - If this can't be found, try to append to the end.
; returns:
; DE_start_sector: sector where file can be saved, is 0 when no space (disk full)
; DE_head:  side (0/1) where the file can be saved
; DE : points to first sector for save-1 (last sector# of preceding file);
; BC : length of file in sectors
; IY : pointer to directory entry to insert BEFORE
;
find_room:
    ld iy,DIR_side_1_mem                ; side 1 directory data
    ld b,000h
    call sectors_for_file               ; # of needed sectors
    ld c,a                              ; in BC
    xor a                               ; start on side 1 (0)
    ld (DE_head),a                      ; save side in dir entry
    ld a,(0f7c0h)                       ; room in directory (last entry is never filled)
    and a                               ; available if next to last entry is empty
    jr nz,no_room_side1                 ; side 1 is full
    xor a                               ; start on side 1 (0)
start_fitting:
    ld (DE_head),a                      ; save side in dir entry
    ld de,DE_size                       ; start at last sector used by directory
    jr start_room_search
try_next_gap:
    ld e,(ix+01bh)                      ; copy number of the last sector of the previous file
    ld d,(ix+01ch)                      ; into DE

start_room_search:
    inc de                              ; possible starting sector for file
    ld l,(iy+019h)                      ; get starting sector of next file in the directory
    ld h,(iy+01ah)
    ld a,h                              ; start sector == 0 means end of dir reached
    or l
    jr nz,try_fit                       ; try if file fits before the next
    call get_sectors_per_side           ; max # of sectors on disk
    xor a                               ; clear carry
    sbc hl,de                           ; subtract starting sector HL = room at the end
    sbc hl,bc                           ; subtract required room
    jr nc,file_fits                     ; room larger than or equal to required!
    jr no_room_side1
try_fit:
    xor a                               ; clear carry
    sbc hl,de                           ; HL now contains #sectors between files
    sbc hl,bc                           ; subtract required #
    jr nc,file_fits                     ; room larger than or equal to required

    push iy                             ; make IX point to previous file info
    pop ix
    ld de,32                        ; make IY point to next file info
    add iy,de
    jr try_next_gap                     ; try to fit there

no_room_side1:
    call is_disk_SS                     ; DS?
    jr z,no_room_found                  ; no
    ld a,(0ffc0h)                       ; room in directory part 2 (last entry is never filled)
    and a                               ; available if next to last entry is empty
    jr nz,no_room_found

    ld a,(DE_head)                      ; already on side 2?
    and a
    jr nz,no_room_found                 ; yes..
    inc a                               ; prep for search on side 2 (head)
    ld iy,DIR_side_2_mem                ; start of dir data side 2
    jr start_fitting

no_room_found:
    ld de,00000h                        ; start sector 0000 means: no room!

file_fits:
    ld (DE_start_sector),de             ; save starting sector for file
    ret

insert_dir_entry:
    ld a,(DE_head)                      ; get side
    and a                               ; side 0?
    jr z,insert_side1
    ld bc,0ffffh                        ; end address of dir side 2
    jr insert_start
insert_side1:
    ld bc,0f7ffh
insert_start:
    push bc                             ; HL points to end of mem to move, end-32
    pop hl                              ; = BC - 20h
    ld de,DE_size
    xor a
    sbc hl,de
    push iy                             ; copy start of mem to move to DE
    pop de
    call BASIC_mem_copy                 ; BASIC memcopy.
                                        ; copies range start-end (DE-HL) to (BC-(HL-DE))-BC, descending
                                        ; DE is unchanged and points to start of 'freed' mem

copy_active_header:                     ; DE points to dest
    ld hl,DE_current_header             ; copy active dir entry
copy_header:
    ld bc,DE_size
    ldir
    ret

disk_read_action:
    ld a,FDC_mode_read
    jr disk_set_action_type
disk_write_action:
    ld a,FDC_mode_write
disk_set_action_type:
    ld (dsk_transfer_cmd_IOtype),a
    jp execute_disk_IO

dir_side1_prep:
    ld hl,00800h                        ; filelength of directory block
    ld (DE_filelen),hl                  ; in working header
    ld h,0f0h                           ; destination address f000h
    ld (DE_transfer),hl
    ld hl,00019h                        ; start sector = 19h = sector 9 track 2
    ld (DE_start_sector),hl
    xor a                               ; side 1 (0)
    ld (DE_head),a
    ret

dir_side2_prep:
    ld hl,DIR_side_2_mem                ; destination
    ld (DE_transfer),hl
    ld hl,00011h                        ; start at sector 17 = sector 1 track 2
    ld (DE_start_sector),hl
    ld a,001h                           ; side 2 (1)
    ld (DE_head),a
    ret

read_directory:
    call disks_on
get_directory:
    call dir_side1_prep
    call disk_read_action
    call is_disk_SS
    ret z
    call dir_side2_prep
    jp disk_read_action

save_directory:
    call dir_side1_prep
    call disk_write_action
    call is_disk_SS
    ret z
    call dir_side2_prep
    jp disk_write_action

txt_save_quotes:
    db              chr_CRLF,'save"',0
txt_load_quotes:
    db              chr_CRLF,'load"',0
txt_wis_quotes:
    db              chr_CRLF,'wis"',0
txt_diskdirvol:
    db              'disk/dir. vol',0
;
; key translate table.
; disable 5 keys (turn into 'BEEP') during input of filenames
;
input_keys_off:
    db              5                   ; 5 pairs
    db              020h, 007h          ; CODE
    db              070h, 007h          ; CLS
    db              068h, 007h          ; shift-CODE
    db              008h, 007h          ; TAB
    db              078h, 007h          ; SHFT-9 ('M')

ask_for_dest_drive:
    ld hl,txt_van_drive                 ; print "van drive " to screen
    call BASIC_Print_text
    ld a,(system_drive)                 ; get FDC drive #
    res 2,a                             ; convert to dos drive number
    ld (DOS_src_drv),a                  ; save as source drive
    and a                               ; zero?
    jr nz,src_1to3                      ; no, source is drive 1
    set 2,a                             ; turn 0 into 4
src_1to3:
    add a,030h                          ; convert to a printable number 1, 2, 3 or 4
    call BAS_CHAR_OUT                   ; and print
    ld hl,txt_naar                      ; text ' naar ' to screen
    call BASIC_Print_text
read_char_for_dest:
    call BASIC_input_char               ; ask for destination
    cp STOP_key                         ; 'Stop'?
    ret z                               ; then exit
    call char_between_1_4               ; is input valid?
    jr c,read_char_for_dest             ; if Carry set: invalid key. read new char
    sub 030h                            ; turn ascii digit to a 0-based number
    res 2,a                             ; remove side2-bit, and make dos drive
    ld hl,DOS_src_drv                   ; get address of source drive
    cp (hl)                             ; is destination equal to source?
    jr z,read_char_for_dest             ; yes, wait for acceptable destination
    and a                               ; destination == 0
    jr nz,dst_1to3                      ; No, so it must be 1
    set 2,a                             ; turn 0 into 4 (set side bit for FDC)
dst_1to3:
    add a,030h                          ; turn into ascii again (1,2,3 or 4)
    call BAS_CHAR_OUT                   ; and print
    sub 030h                            ; back to 0-based
    res 2,a                             ; remove side bit
    ld (DOS_dst_drv),a                  ; store as destination drive
    call BAS_CURSOR_OFF
    jp BASIC_print_CRLF

disk_copy_disk:
    ld hl,txt_copieren                  ; ask confirmation
    call prt_txt_ask_Ja_Nee             ; if no this call returns to basic
    call CLEAR_dfff                     ; use full RAM
    call BASIC_FAST_LIST_MODE
    call ask_for_dest_drive
    cp STOP_key                         ; terminate when 'Stop' was pressed
    ret z
    ld sp,BASIC_top_of_temp_storage     ; move stack to scratch area
    call start_fdc                      ; drive(s) on
    call copy_full_disk
exit_reset_sys_drive:
    ld a,(DOS_src_drv)
    ld (system_drive),a

clear_basic_and_exit:
    call clear_FDC_and_interrupts
    xor a                               ; reset full sector flag
    ld (DOS_only_full_sectors),a
    ld hl,txt_Klaar                     ; 'Klaar' to screen
    call BASIC_Print_text
    ld hl,(BASIC_start_of_prog)         ; reset start of basic address
    ld (hl),a
    inc hl
    ld (hl),a
    inc hl
    call set_end_of_basic_prog
    jp return_to_basic

txt_Klaar:
    db      chr_CRLF,chr_CYAN,"Klaar",0

set_sys_dr_and_read_dir:
    ld (system_drive),a
    ld (dsk_recall_cmd_drive),a
    call MON_DSK_calibrate              ; calibrate command
    call get_directory                  ; load dir
    ret

set_extension_JWS:
    ld hl,txt_JWS
set_extension:
    ld de,DE_extension

; copy 3 bytes from DE to HL
; DE contains pointer to the source.
; can be a jp instruction (3 bytes)
; HL contains the destination address.
; 3 bytes are copied from (DE) to (HL)
; also used to set 'JWS' or 'OBJ' as extension
copy_3_bytes:
    ld bc,00003h        ; 3 bytes
    ldir                ; and copy them
    ret

copy_a_file:
    call list_fileinfo_and_load         ; display name and load file
    ld de,MON_transfer_address          ; end address is in HL, put start address in DE
    call copy_active_header             ; copy active header to temp area
    ld a,(DOS_dst_drv)                  ; prep dest drive
    call set_sys_dr_and_read_dir
    ld hl,MON_transfer_address          ; get header back
    call to_current_header
    call find_file_in_dir
    call c,set_extension_JWS            ; if file exists set file extension to 'JWS'
    call write_file
    call save_directory
    ret

copy_full_disk:
    call get_directory
    ld a,001h                           ; only full sectors
    ld (DOS_only_full_sectors),a
    ld hl,DIR_side_1_mem                ; dir for side 1
copy_dsk_loop:
    push hl                             ; save pointer to dir entry
    ld a,(hl)                           ; zero means no more files
    and a
    jr z,switch_side                    ; try next side, if DS
    call to_current_header              ; copy entry to working header
    call copy_a_file                    ; copy the file
    ld a,(DOS_src_drv)                  ; back to source drive
    call set_sys_dr_and_read_dir
    pop hl                              ; skip to next dir entry
    ld de,DE_size                       ; by adding 32 (size of dir entry)
    add hl,de
    jr copy_dsk_loop                    ; and loop
switch_side:
    pop de                              ; previous entry in DE
    call is_disk_SS                     ; If disk is SS
    ret z                               ; copy is done
    ld hl,DIR_side_2_mem                ; pointer to 2nd side dir entries
    ld a,d                              ; was last entry on side 2 (bit 3 set)
    bit 3,a
    jr z,copy_dsk_loop                  ; no, do 2nd side
    ret                                 ; copy done!

txt_copieren:
    db          chr_CRLF,chr_YELLOW,"copieren",0
txt_van_drive:
    db          chr_CYAN,"van drive ",0
txt_naar:
    db          " naar ",0

start_fdc:
    ld a,(system_drive)                 ; set recall command drive #
    ld (dsk_recall_cmd_drive),a
    di                                  ; interrupts off

    ld a,001h                           ; command 00000001 = CTC interrupts OFF
    out (CTC_CHANNEL0),a                ; disable all CTC interrupts
    out (CTC_CHANNEL1),a
    out (CTC_CHANNEL2),a
    out (CTC_CHANNEL3),a
    call MON_DSK_init                   ; sets CTC's etc
    call MON_DSK_motor_on
    jp MON_DSK_delay_342ms              ; allow to spin-up and return

check_write_enable:
    ld a,(system_drive)                 ; set target drive
    ld (target_drive_for_status),a
    ld hl,dsk_cmd_get_status            ; get status
    call MON_DSK_send_command
    ld b,001h                           ; only one result byte needed
    call MON_DSK_read_status_bytes      ; will be stored at MON_working_mem
    ld a,(MON_working_mem)              ; read result
    bit 6,a                             ; bit 6 = write protect (1=protected)
    ret z                               ; can write!
    ld hl,txt_disk_beveiligd            ; pointer to error message
    ld e,JWS_ERR_WRITE_PROTECTED        ; 33 = write protected error
    jp rest_ints_and_error              ; print and terminate gracefully

clear_FDC_and_interrupts:
    ld a,003h                           ; command 00000011 = CTC reset
    out (CTC_CHANNEL0),a                ; Reset CTC Channel 0
    rst 20h                             ; MONITOR enable keyboard routine
    xor a                               ; 00000000 to FDC
    out (FDC_CONTROL),a                 ;
    ei
    ret
;
; perform disk IO
;  E: # of sectors to write
; HL: transfer address??
;
perform_disk_IO:
    call set_disk_IO_interrupts         ; prep FDC ready and error hooks
    ld hl,dsk_transfer_cmd              ; send transfer command
    call send_FDC_command
    ld hl,(dsk_transfer_adr)
    ld a,(dsk_transfer_cmd_IOtype)      ; get IO type
    res 7,a                             ; reset bit 7
    cp FDC_mode_write                   ; writing?
    jr z,do_write
disk_IO_read_loop:
    ld a,e                              ; sectorcount
    jr z,disk_IO_done                   ; first time: NZ (not write) next time(s) set according to dec e
    cp 1                                ; last sector?
    jr nz,read_full_sec                 ; no, read full sector
    ld a,(DOS_last_sector_flag)         ; partial?
    bit 1,a
    jr z,read_full_sec                  ; no, read full sector
    ld a,(DE_filelen_LO)                ; bytes to read
le741h:
    ld b,a                              ; set b to # of bytes
read_full_sec:
    call MON_read_sector
    dec e
    jr disk_IO_read_loop
do_write:
    call MON_write_sector
    dec e
    jr nz,do_write
disk_IO_done:
    ld a,00eh                           ; 00001110 = Motor on | reset | terminal count
    out (FDC_CONTROL),a
    ret

send_FDC_command:
    call MON_DSK_send_command           ; Send command
    ld a,0c5h                           ; 11000101 = INT ON | COUNTER | next byte is time const | CTRLWRD
    out (CTC_CHANNEL1),a
    ld a,001h                           ; set time constant at 1
    out (CTC_CHANNEL1),a
    ld c,FDC_STATUS                     ; clear status
    ld a,00dh                           ; 00001101 = Motor ON | RESET | ENABLE
    out (FDC_CONTROL),a
    ret

set_disk_IO_interrupts:
    ld hl,MON_dummy_handler             ; disk io finished vector
    ld (MON_interrupt_ch0),hl
    ld hl,disk_not_ready                ; FDC not ready vector
    ld (MON_interrupt_ch1),hl
    ret
;
; returns end address of load in HL
;
execute_disk_IO:
    xor a                               ; clear carry for sbc later on
    ; turn linear startsector # into disk track and sector
    ld hl,(DE_start_sector)             ; get startsector #
    ld de,00010h                        ; 16 sectors/track
    ld b,001h                           ; start at track 1
IO_trk_count_loop:
    sbc hl,de                           ; subtract a track
    jr c,IO_trk_count_exit              ; not a full track
    inc b                               ; in full track
    jr IO_trk_count_loop                ; and loop
IO_trk_count_exit:
    add hl,de                           ; not a full track, add 16 to get remainder
    ld a,l                              ; remainder zero?
    and a
    jr nz,IO_not_full_trk               ; No
    ld a,010h                           ; start with sector 16
    dec b                               ; on previous track (forces skip to next track!)
; a contains starting sector #
IO_not_full_trk:
    ld c,a                              ; 604d = sector 604e = track
    ld (DE_sec_trk),bc
    ld a,(system_drive)                 ; copy active drive to
    ld (dsk_seek_cmd_drive),a           ; disk 'seek' command
    ld (dsk_recall_cmd_drive),a         ; disk 'recall' command
    ld (dsk_transfer_cmd_drive),a       ; disk 'transfer' command
    ld b,a                              ; store in b
    ld a,(DE_head)                      ; copy head into
    ld (dsk_transfer_cmd_head),a        ; disk 'transfer' command
    and a                               ; side 0?
    jr z,dsk_IO_set_sec_trk             ; yes
    ld a,b                              ; get drive #
    xor 004h                            ; turn into FDC drive# for head 1 (4-7) and set in
    ld (dsk_transfer_cmd_drive),a       ; disk 'transfer' command
dsk_IO_set_sec_trk:
    ld a,(DE_sec)
    ld (dsk_transfer_cmd_sec),a
    ld a,(DE_trk)
    ld (dsk_transfer_cmd_trk),a
    ld hl,(DE_transfer)
    ld (dsk_transfer_adr),hl
    push hl                             ; save transfer address for next sector
    ld hl,DOS_last_sector_flag          ; reset last sector flag
    res 1,(hl)
    pop hl                              ; transfer address ACTION: push/pop HL unnessecary?
    call sectors_for_file
    ld (DE_sec_count),a                 ; store # of sectors
disk_IO_loop:
    ld a,(DE_sec_count)                 ; return when last sector is done
    and a
    ret z

    ld d,a                              ; secs_to_process
    ld a,(DE_sec)                       ; get sector counter
    dec a                               ; predecrement for inc in loop
    ld e,000h                           ; DE = secs_to_process--secs_for_trk
IO_sec_count_loop:
    inc a                               ; a = sector to write to
    inc e                               ; DE = secs_to_process_01
    dec d                               ; DE = secs_to_process-1_01
    jr z,IO_last_sector_flag            ; if last sector, set correct flags
    cp 16                               ; track full?
    jr nz,IO_sec_count_loop             ; add one more sector to batch
;
;   E contains # of sectors to write to the current track
;   HL contains transfer address
;
disk_IO_execute:
    ld a,d                              ; store sectors left to process
    ld (DE_sec_count),a
    call go_track                       ; head to proper track
    xor a                               ; assume full sector IO
    ld b,a
    call disk_do_IO                     ; transfer E sectors
    ld (dsk_transfer_adr),hl            ; save transfer address
    call get_disk_status                ; read status from FDC
    ld a,001h                           ; next IO will be on sector 1
    ld (dsk_transfer_cmd_sec),a
    ld (DE_sec),a
    ld a,(dsk_transfer_cmd_trk)         ; of the next track
    inc a
    ld (dsk_transfer_cmd_trk),a
    jr disk_IO_loop

IO_last_sector_flag:
    push hl                             ; save transfer address
    ld a,(DOS_only_full_sectors)        ; force only full sectors?
    and a
    jr nz,disk_IO_full_sectors          ; yes!
    ld hl,DOS_last_sector_flag          ; indicate last sector may not be full
    set 1,(hl)
disk_IO_full_sectors:
    pop hl                              ; transfer address
    jr disk_IO_execute

txt_run:
    db          'run"',0
txt_check_disk:
    db          'controleer disk',0
txt_disk_beveiligd:
    db          'disk beveiligd',0

dsk_cmd_get_status:
    db          002h,004h,0             ; 3 bytes: len = 2, command = 4, drive #
target_drive_for_status:        equ dsk_cmd_get_status+2

ask_overwrite_file:
    ld hl,(DE_transfer)                 ; copy file start to system
    ld (MON_transfer_address),hl
    ld hl,(DE_filelen)                  ; copy file length
    ld (MON_file_length),hl
    push ix                             ; save pointer to directory entry
    call disks_off
    pop hl
    call to_current_header              ; activate the file
    call print_fileinfo                 ; display info
    call mon_file_len_adr_to_dir        ; system file start and len to working item

    ld hl,BASIC_txt_Overheen            ; 'Overheen (J/N)?' to screen
    call prt_txt_ask_Ja_Nee             ; N/No terminates and exits
                                        ; if J/Yes execution continues

remove_and_recheck:
    call remove_from_dir
    call disks_on
    call is_basic_running
    jp z,save_check_existing            ; no, check/ask for existing file again, "*" in name may match more than 1
    jp save_file                    ; when basic is running

txt_crunch:
    db          chr_CRLF,chr_YELLOW,"crunch",0

prt_Ja_and_return:
    ld hl,BASIC_txt_Ja
    jp BASIC_Print_text

prt_Nee_and_terminate:
    ld hl,BASIC_txt_Nee
    ld e,'Q'                            ; Quit error code
    jp rest_ints_and_error
;
; print text and ask yes/No
; returns with Ja
; terminates at No
;
prt_txt_ask_Ja_Nee:
    call BASIC_Print_text
    ld hl,BASIC_txt_ja_nee
    call BASIC_Print_text
    call BASIC_input_char
    cp STOP_key                         ; 'Stop'?
    jr z,prt_Nee_and_terminate          ; Same as NO: do clean termination
    res 5,a                             ; make upper case
    cp 'J'                              ; Yes?
    jr nz,prt_Nee_and_terminate         ; Not 'J' so 'No': clean termination
    jr prt_Ja_and_return                ; print Ja and return

txt_kant:
    db  chr_CRLF,chr_CYAN,"kant ",0

CLEAR_dfff:
    ld hl,0dfffh                        ; do a "CLEAR 0xDFFF" to protect JWS-DOS code and data
    ld (BASIC_Top_of_Mem),hl            ; store as highest addres that BASIC may use
    ld de,00032h                        ; preserve the standard 50 bytes of string space
    sbc hl,de
    ld (BASIC_Start_Stringspace),hl     ; store as start of string space
    ret
; =============================================================================================
; =============================================================================================
;  DEAD CODE??
    ld hl,P2000_modelM_attributes       ; start of attribute meory to clear
    ld bc,007ffh                        ; 2048-1 bytes to clear
    ld (hl),a                           ; set first byte
    inc de                              ; skip one; 1st byte already done
    ldir                                ; and fill the rest
    ld hl,BASIC_tab_spacing             ; Set tab size for P2000M
    ld (hl),DOS_P2000M_tab_size         ;
is_P2000T:
    ld de,BASIC_command_hook            ; Insert DOS command hook for BASIC ($60D0)
    ld hl,dos_hook                      ; start address for dos-hook
    call copy_3_bytes                   ; and set!

    ld hl,(BASIC_string_save)           ; get string save JP address
    ld (string_save_Backup),hl          ; make backup
    ld hl,(BASIC_csave_vector)          ; get original csave address
    ld (csave_address_backup),hl        ; make backup

    call CLEAR_dfff

; remove ?Usr(0), used to activate JWS-DOS from usr-vector table
    ld b,00ah                           ; 10 entries in table
    ld de,00005h                        ; vector to find
    ld ix,BASIC_Usr_vectors             ; start of usr-vector table
usr_rep_lp:
    ld l,(ix+000h)                      ; get vector in HL
    ld h,(ix+001h)
    call BASIC_compare_HL_DE            ; Compare HL and DE: Z if HL==DE, S&C when HL<DE, NZ&P&NC when HL>DE
    jr z,usr_replace                    ; found, remove!
    inc ix                              ; next vector
    inc ix
    djnz usr_rep_lp                     ; and loop max 10 times
    jr credits_and_go                   ; not found, exit
usr_replace:
    ld hl,0289ch                        ; copy standard dummy usr vector
    ld (ix+000h),l                      ; over usr(0)
    ld (ix+001h),h
;  END of DEAD code ??
; =============================================================================================
; =============================================================================================

credits_and_go:
    ld hl,Intro_text                        ; print credits
    call BASIC_print_text_2
    ld hl,(BASIC_Start_Stringspace)         ; top of mem
    ld de,-06557h                           ; subtract start of basic area
    add hl,de                               ;
    call BASIC_print_hl_decimal             ; print getal in HL??  ACTION: check in BASIC code
    ld hl,BASIC_txt_bytes_vrij
    call BASIC_print_text_2
    ld sp,06266h                            ; stack in basic input buffer area
    call 01ccdh                             ; jump into normal basic startup?? ACTION: check in BASIC code

try_run_autorunbas:
    call disks_on
    ld hl,txt_autorun_bas                   ; set filename to autorun.bas
    ld de,DE_filename
    ld bc,00014h                            ; 20 bytes, should be 19??
    ldir
    call get_directory
    call find_file_in_dir
    jp nc,no_autorun_file
    push ix
    pop hl
    call to_current_header
    call load_found_file
    jp BASIC_RUN

no_autorun_file:
    call disks_off
    jp 01fc4h                               ; Start BASIC normally

disk_autorun:
    ld hl,txt_autorun
    call BASIC_Print_text
    call BASIC_print_CRLF
    jp try_run_autorunbas

txt_autorun:
    db                      chr_CYAN,"Autorun",0
txt_ramdisk_aan:
    db                      chr_CRLF,chr_GREEN,"Ramdisk aan",0
txt_ramdisk_uit:
    db                      chr_CRLF,chr_GREEN,"Ramdisk uit",0
txt_autorun_bas:
    db                      "AUTORUN         BAS"

handle_dos_keywords:
    ld (DOS_basic_stack),sp             ; save basic stack
    ld (DOS_save_input_ptr),hl          ; and pointer to next char
    ld (BASIC_input_ptr),hl             ; set active line number (maybe not necessary)
    ld a,(hl)                           ; get next charater
    cp ':'                              ; colon?
    jr z,is_it_DOS_command              ; Yes, check for one of DOS' keywords
    or a                                ; Zero?
    jp nz,02463h                        ; No, continue with BASIC TODO: details
                                        ; zero is end of line or end of program.. find out!
    inc hl                              ; next char
    ld a,(hl)
    inc hl                              ; next char
    or (hl)                             ; 3 zero's?
    jp z,not_dos_command                ; yes.. end of program, leave it to BASIC.
    inc hl                              ; no, so new line
    ld e,(hl)                           ; get hi-byte
    inc hl                              ; and lo-byte
    ld d,(hl)
    ld (BASIC_active_program_line),de   ; set active line#
is_it_DOS_command:
    call BASIC_get_next_ch
    ld a,(hl)                           ; next character
    cp '#'                              ; DOS commands start with #
    jr nz,not_dos_command               ; Not dos command, exit to Basic

    call parse_dos_command
    jr nc,execute_dos_command

dos_command_error:
    ld e,BASIC_ERR_SYNTAX               ; 02 = Syntax error: unknown keyword after #, or error during execution
    jp handle_error

not_dos_command:
    ld sp,(DOS_basic_stack)             ; restore stack
    ld hl,(DOS_save_input_ptr)          ; and input pointer
    ret

execute_dos_command:
    ld a,c                              ; command #*3 = offset in jumptable
le9c0h:
    rla                                 ; x2
    add a,c                             ; x3
    ld c,a                              ; to BC
le9c3h:
    ld b,000h                           ; zero B for add
    ld hl,dos_cmd_jumptable             ; add offset to table start
    add hl,bc
    jp (hl)                             ; and jump there

is_name_present:
    ld hl,(DOS_next_input_ptr)          ; start parsing here
    call BASIC_get_current_ch           ; carry set: next char is digit
    ret nc                              ; C not set then filename can be parsed, return!
    jr dos_command_error                ; otherwise handle syntax/parse error

dos_hook:
    call handle_dos_keywords

;
; returns:
;   C (carry set): unknown command
;   NC  (no carry): found, register C contains command number
;
parse_dos_command:
    ld de,dos_cmd_commands              ; start of commands table
    inc hl                              ; next character of command
    ld c,000h                           ; command index starts at 0

start_cmd_parse:
    push hl                             ; save pointer to next character
parse_char_loop:
    ld b,(hl)                           ; b = char
    ld a,(de)                           ; char from command table
    cp '$'                              ; $ = end of command
    jr z,command_found                  ; if $, command was found
    cp b                                ; compare characters
    jr nz,next_command                  ; No match, try next command
    inc hl                              ; next character to compare
    inc de
    jr parse_char_loop

command_found:
    ld (DOS_next_input_ptr),hl          ; save pointer to last parsed char
    pop hl                              ; get original input pointer back
    ret

command_not_found:
    scf                                 ; Carry set means error or not found
    pop hl                              ; get original input pointer back
    ret

next_command:
    inc c                               ; adjust command #
next_cmd_loop:
    inc de                              ; get next char in commmand table
    ld a,(de)
    cp '_'                              ; End of table reached?
    jr z,command_not_found
    cp '$'                              ; end of current command?
    jr nz,next_cmd_loop                 ; no
    inc de                              ; yes, move past $
    pop hl                              ; start of command to parse
    jr start_cmd_parse

dos_cmd_commands:
    db              "LOAD$"
    db              "SAVE$"
    db              089h,'$'            ; 089h = token for 'RUN'
    db              "ZOEK($"
    db              "WIS$"
    db              "VP$"
    db              "SYS($"
    db              '_'
dos_cmd_jumptable:
    jp dos_cmd_load
    jp dos_cmd_save
    jp dos_cmd_run
    jp dos_cmd_zoek
    jp dos_cmd_wis
    jp dos_cmd_vp
    jp dos_cmd_sys

dos_cmd_load:
    call is_name_present                ; exits with error if no filename follows LOAD command
    push hl                             ; save pointer input
    ld hl,cmd_load_hook                 ; redirect string save and csave vectors to our own code
    ld (BASIC_string_save),hl
    ld (BASIC_csave_vector),hl
    pop hl                              ; input buffer
lea43h:
    call BASIC_csave                    ; call CSAVE, will This will parse filename and fill
                                        ; in the file header at 0x6130
                                        ; then jumps through CALL 60CC to actually perform the save
                                        ; and this will jump to label below (the vector we just changed)
                                        ; It will not do much; we just want the filename/header info!

cmd_load_hook:
    pop af                              ; Are we loading a (string) array (A==1) ?
    dec a
    jp z,load_array                     ; yes! try to load into array
    call find_and_load_file             ; no, just load the file
    jr exit_save_load_cmd

dos_cmd_save:
    call is_name_present                ; exits with error if no filename follows SAVE command
    push hl                             ; save pointer to input
    ld hl,cmd_save_hook                 ; redirect string save and csave vectors to our own code
    ld (BASIC_string_save),hl
    ld (BASIC_csave_vector),hl
    pop hl                              ; pointer to next input
    call BASIC_csave                    ; call CSAVE, will This will parse filename and fill
                                        ; in the file header at 0x6130
                                        ; then jumps through CALL 60CC to actually perform the save
                                        ; and this will jump to label below (the vector we just changed)
                                        ; It will not do much; we just want the filename/header info!
cmd_save_hook:
    pop af                              ; Are we saving an (string) Array (A==1) ?
    dec a
    jp z,save_array                     ; yes! try to save the array
    call try_save_file                  ; no, try to save file (ask for overwrite etc)

exit_save_load_cmd:
    call restore_basic_vectors          ; and get pointer to next  input char in HL
    call BASIC_CMD_DATA                 ; skip trailing constants etc
    ld (DOS_save_input_ptr),hl          ; save where basic left off
    ld sp,(DOS_basic_stack)             ; restore BASIC stackpointer
    jp handle_dos_keywords              ; and see if we know the next command

restore_basic_vectors:
    ld hl,(string_save_Backup)
    ld (BASIC_string_save),hl
    ld hl,(csave_address_backup)
    ld (BASIC_csave_vector),hl
    ld hl,(DOS_next_input_ptr)          ; let BASIC pick up where DOS stopped
    ret

try_save_file:
    call read_directory
;
; save, ask for overwrite if necessary/allowed
;
save_file:
    call mon_file_to_dir_file           ; prep dir header
    call find_file_in_dir
    jp nc,save_file_and_dir             ; file doesn't exist. save it.

                                        ; file exists, ask overwrite or not??
    ld a,(BASIC_Cassette_flags)         ; bit 7 set: ask nothing during (C)SAVE
    bit 7,a                             ;
    jp z,remove_and_recheck             ; bit not set, silently remove and try again
    jp ask_overwrite_file               ; ask

mon_file_to_dir_file:
    ld hl,MON_file_name_part1           ; copy 1st 8 chars of filename
    ld de,DE_filename
    ld bc,00008h
    ldir
    ld hl,MON_file_name_part2           ; copy 2nd 8 characters
    ld c,008h
    ldir
    ld hl,MON_file_extension            ; copy extension
    call copy_3_bytes
mon_file_len_adr_to_dir:
    ld hl,(MON_transfer_address)        ; copy transfer address to active header
    ld (DE_transfer),hl
    ld hl,(MON_file_length)             ; copy file lengthh
    ld (DE_filelen),hl
    ret

find_file_and_set_address:
    call mon_file_to_dir_file           ; copy file info to active dir header
    call search_file                    ; search for file, terminates with error when not found
    ld hl,(MON_transfer_address)        ; overwrite stored transfer address
    ld (DE_transfer),hl                 ; with address we need
    ret

; entry for API
load_file:
    call find_file_and_set_address
    jp read_data_and_disks_off
;
; loads file if it exists and fits
; exits with error if not
;
find_and_load_file:
    call read_directory
    call find_file_and_set_address      ; if not found, this one exits to basic
    xor a                               ; clear carry
    ld de,(DE_filelen)                  ; len of found file
    ld hl,(BASIC_real_file_len)         ; calculate size of data to load
    sbc hl,de                           ; in hl
    jp load_file_data_if_fits
;
; HL contains pointer to inputbuffer
; uses return address address on stack to compare input buffer with desired character
; found in (SP)
;
next_char_valid:
    ld a,(hl)                           ; get input char
    ex (sp),hl                          ; Stackpointer pointing to same character?
    cp (hl)
    jp nz,syntax_err                    ; No, error!!
    inc hl                              ; Yes, remove from stack
    ex (sp),hl
    jp BASIC_get_next_ch                ; skip whitespaces an point to next inpout char

syntax_err:
    ld e,BASIC_ERR_SYNTAX               ; 2 = SYNTAX ERROR
    jr rest_ints_and_error

disk_full_error:
    ld e,JWS_ERR_DISK_FULL              ; DOS error code 'disk full'
    ld hl,txt_diskdirvol                ; 'disk/dir. vol' message
    jr rest_ints_and_error

file_too_large_error:
    ld hl,BASIC_txt_out_of_memory
    ld e,JWS_ERR_OUT_OF_MEMORY          ; DOS out of memory error code
    jr rest_ints_and_error

file_not_found_error:
    call disks_off
    ld e,JWS_ERR_NOT_FOUND
    ld hl,BASIC_txt_Niet_Gevonden
    jr rest_ints_and_error

disk_not_ready:
    ld hl,txt_check_disk                ; show error message
    ld e,JWS_ERR_DISK_FAILURE           ; diskdrive error

rest_ints_and_error:
    call clear_FDC_and_interrupts       ; motor off, interrupts on etc

handle_error:
    ld d,0                              ; reset d
    ld a,e                              ; exit/error code
    cp 'Q'                              ; recoverable error 'Quit' ?
    jr z,error_resume                   ; yes, continue
    bit 5,a                             ; bit 5 not set?
    jr z,error_to_basic                 ; then error and exit to basic

error_resume:
    ld (BASIC_error_code),a             ; store BASIC errorcode
    call is_basic_running               ; if running a program
    jr nz,error_to_basic                ; hand error over to the interpreter
    ld a,chr_PURPLE
    call BAS_CHAR_OUT                   ; set color
    call BASIC_Print_text               ; print error message
    jp return_to_basic

error_to_basic:
    call restore_basic_vectors
    jp exit_with_basic_error

;
; register e contains drive #
; returns with system drive set when # is in range 0..3 (1-4)
; exits with error 35 if not in range
;
set_sysdrive:
    ld a,e                              ; result in A
    and a                               ; zero?
    jr nz,drive_not_0                   ; No
    set 2,a                             ; turn 0 into 4
drive_not_0:
    add a,'0'                           ; turn into ascii digit
    call char_between_1_4               ; valid drive # carry set if not
    jr c,drive_invalid
    and 003h                            ; back to real number
    ld (system_drive),a                 ; and set
    ret
drive_invalid:
    ld e,JWS_ERR_ILLEGAL_DRIVE          ; error 35: drive out of range
    jr handle_error

; when saving an array, BASIC has found the array in memory
; and placed the start address in MON_transfer_address
; and the size in MON_file_length
save_array:
    inc a                               ; a was 0, reset array save flag. why? is not used at all...
    ld hl,(MON_file_length)             ; get size of array
    ld de,(MON_transfer_address)        ; get start of array
    add hl,de                           ; find end address of array in memory
    inc de                              ; point to start of array + 1 (skip type)
    ld bc,00000h                        ; actual length
    ld (MON_file_length),bc             ; reset length
save_next_loop:
    inc bc                              ; 1 more element to save
    push bc                             ; save length
    push de                             ; begin of array data pointer
    dec hl                              ; HL -= 3
    dec hl
    dec hl
    push hl                             ; save pointer to array element
    ld a,(hl)                           ; get length
    or a
    jr z,save_move_next                 ; empty element

    ld c,a                              ; length in bc
    ld b,000h
    ld hl,(MON_file_length)             ; add to file length
    add hl,bc
    ld (MON_file_length),hl
    call BASIC_find_room_for_string     ; copy string to string space
    pop hl                              ; pointer to current element
    call copy_string
    push hl                             ; pointer to element

save_move_next:
    pop hl                              ; current pointer to array element
    pop de                              ; begin of array in mem
    pop bc                              ; current size
    call BASIC_compare_HL_DE            ; end reached?
    jr nc,save_next_loop                ; no, process next element

    push bc                             ; number of elements in the array
    call BASIC_garbage_collect          ; clean up memory and variables
    pop bc                              ; get # of elements
    ld hl,(MON_file_length)             ; make room for ??
    inc hl
    inc hl
    add hl,bc                           ; and 1 byte per element
    ld (MON_file_length),hl
    ld (BASIC_real_file_len),hl
    ld (BASIC_binary_start),hl
    ld hl,(BASIC_first_free_string)     ; free string space
    dec hl                              ; 1 byte extra (for # of elements)
    or a
    sbc hl,bc                           ; need 1 byte length for each string
    ld de,(BASIC_Start_Stringspace)
    call BASIC_compare_HL_DE
    jp c,BASIC_no_room_for_string       ; no room for extra data
    ld de,(MON_transfer_address)        ; original address in DE
    ld (MON_transfer_address),hl        ; save start of string data address
    ld (hl),c                           ; # of elements in data space
    inc hl
    ld (hl),b
    inc hl
    ex de,hl                            ; save dest in de, HL contains orignal address
copy_len_loop:
    ldi                                 ; copy length of string to data block to save
    inc hl                              ; skip over memory pointer of element
    inc hl
    ld a,b                              ; BC == 0 ?
    or c
    jr nz,copy_len_loop                 ; no, do next length byte
    call try_save_file
    jr exit_save_load
;
; when loading an array, BASIC has found the array in memory
; and placed the start address in MON_transfer_address
; and the size in MON_file_length
;
; string array is stored as:
; 1             word    #of elements
; #of elements  bytes   with lengths of strings
; strings       (only with length > 0)
;
load_array:
    ld bc,(MON_file_length)             ; Clear the current array
    dec bc
    ld hl,(MON_transfer_address)        ; load adress in hl
    push hl                             ; save transfer address on stack
    push bc                             ; len -1 on stack
    ld (hl),000h                        ; place zero at start of load
    push hl                             ; address + 1 in DE
    pop de
    inc de
    ldir                                ; fill all bytes of array
    call BASIC_garbage_collect          ; BASIC memory clean-up of vars/strings

    ld de,(BASIC_Start_Stringspace)     ; start address of string space
    ld hl,(BASIC_first_free_string)     ; get first free address for strings counts downwards!
    sbc hl,de                           ; calculate available room for new strings
    ld (MON_file_length),hl             ; save as file len
    ld (BASIC_real_file_len),hl         ; and as real(payload) len
    ex de,hl                            ; get start of strings address in hl
    ld (MON_transfer_address),hl        ; start of strings as transfer address
    push de                             ; string table length on stack
    call find_and_load_file             ; load file if it fits, exits with error if not

    pop de                              ; size of stringspace
    ld hl,(MON_transfer_address)        ; start of array data

    ld c,(hl)                           ; get # of loaded strings in BC
    inc hl
    ld b,(hl)
    inc hl                              ; HL points to 1st length byte
    ex de,hl                            ; save pointer to loaded data (length byte) in DE
    ld h,b                              ; HL = BC * 3
    ld l,c
    add hl,hl
    add hl,bc
    ex de,hl                            ; Needed room to DE
    ex (sp),hl                          ; place data pointer on stack, get needed space-1 in HL
    dec de                              ; calculated len -1
    call BASIC_compare_HL_DE            ; Carry: HL<DE means file len < string table len
    ld e,007h                           ; 7 = out of memory error
    jp c,handle_error                   ; file has more data than the destination array can hold!

    pop hl                              ; data pointer (to 1st len byte) from stack
    pop de                              ; get dest array start address from stack
    push hl                             ; save data pointer on stack
    ld (BASIC_first_free_string),hl     ; set pointer to first free string location
    add hl,bc                           ; add # of strings to HL: now points to 1st string!
    ex (sp),hl                          ;   HL: pointer to length byte, pointer to string start on stack

do_next_str_element:
    ld a,(hl)                           ; get length
    inc hl                              ; point to next item
    ex (sp),hl                          ; HL: pointer to string start, pointer to pointer to next length byte on stack
    ld (de),a                           ; save length in string descriptor
    inc de
    ex de,hl                            ; DE: pointer to string start, HL points to string descriptor
    ld (hl),e                           ; copy string address into descriptor
    inc hl
    ld (hl),d
    inc hl
    ex de,hl                            ; HL: pointer to string start, DE points to next string descriptor
    add a,l                             ; make HL pointg to next string by adding length
    ld l,a
    jr nc,next_element
    inc h
next_element:
    ex (sp),hl                          ; HL: pointer to next length byte, pointer to next string on stack
    dec bc                              ; one less element to do
    ld a,c
    or b
    jr nz,do_next_str_element           ; BC not zero: more to do

exit_save_load:
    jp exit_save_load_cmd

dos_cmd_zoek:
    ld hl,(DOS_next_input_ptr)          ; get drive number
    call calc_expression                ; in A and DE
    call set_sysdrive                   ; exits with error if drive out of range
    call list_dir_from_zoek             ; jump into zoek routine
    ld a,chr_CRLF                       ; new line
    call BAS_CHAR_OUT
    jr exit_save_load
;
; VP: load program while preserving variables
; ('voortzetten programma'?)
;
dos_cmd_vp:
    call BASIC_garbage_collect          ; Clean up memory and vars
    ld hl,(DOS_next_input_ptr)          ; advance to next input character
    call BASIC_get_current_ch
    call prepare_fileheader             ; parse filename etc
    call mon_file_to_dir_file           ; convert to jwsdos name
    call read_directory                 ; and find the file
    call search_file
    ld hl,(BASIC_start_of_prog)         ; set load address in header
    ld (DE_transfer),hl
    xor a                               ; clear carry for calculation of avaialble room
    ld hl,(BASIC_end_of_arrays)         ; get end of arrays (end of used memory)
    ld de,(BASIC_start_of_vars)         ; end of basic == start of vars
    sbc hl,de                           ; calculate memory used for vars
    ld de,(BASIC_start_of_prog)         ; add to start address
    add hl,de                           ; to find end address
    ld de,(DE_filelen)                  ; add file length
    add hl,de                           ; HL contains required end address
    ld de,(BASIC_Start_Stringspace)     ; get max end address in DE
    call BASIC_compare_HL_DE            ; compare needed and avaialable
    jr c,vp_file_fits                   ; if needed (HL) < available (DE) then the file fits
    ld e,JWS_ERR_OUT_OF_MEMORY          ; out of memory error
    jp rest_ints_and_error

vp_file_fits:
    call move_embedded_strings          ; rescue embedded strings to string space
    ld hl,(BASIC_start_of_prog)         ; does the file still fit?
    ld de,(DE_filelen)                  ; calculate highest load address in HL
    add hl,de
    ld de,(BASIC_start_of_vars)         ; compare with first variables address
    call BASIC_compare_HL_DE
    jr c,move_vars_down                 ; varstart < current varstart, then move down

    sbc hl,de                           ; calculate difference
    push hl                             ; save distance
    ld bc,(BASIC_end_of_arrays)         ; calculate dest address
    push bc                             ; save source address
    add hl,bc                           ; add delta
    push hl                             ; push dest address
    pop bc                              ; dest in BC
    pop hl                              ; bytes to move in HL, DE holds source end address
    call BASIC_mem_copy                 ; copy the data
    pop bc                              ; get delta from stack
    ld hl,(BASIC_start_of_vars)         ; adjust start of vars
    add hl,bc
    ld (BASIC_start_of_vars),hl
    ld hl,(BASIC_start_of_arrays)       ; adjust start of arrays
    add hl,bc
    ld (BASIC_start_of_arrays),hl
    ld hl,(BASIC_end_of_arrays)         ; adjust end of arrays
    add hl,bc
    ld (BASIC_end_of_arrays),hl
    jr lece7h

move_vars_down:
    push de                             ; save varstart pointer
    ex de,hl                            ; HL varstart, DE highest address
    xor a                               ; clear carry
    sbc hl,de                           ; how many positions must the vars be shifted?
    pop de                              ; get varstart pointer
    push hl                             ; push delta
    ex de,hl                            ; DE: delta, HL: varstart pointer (source)
    sbc hl,de                           ; calculate dest and put in BC
    push hl
    pop bc

    ld de,(BASIC_start_of_vars)         ; Move all variables and arrays
    ld hl,(BASIC_end_of_arrays)
    call move_bytes_loop

    pop bc                              ; get the delta from stack
    ld hl,(BASIC_start_of_vars)         ; adjust start of vars
    sbc hl,bc
    ld (BASIC_start_of_vars),hl
    ld hl,(BASIC_start_of_arrays)       ; adjust start of arrays
    sbc hl,bc
    ld (BASIC_start_of_arrays),hl
    ld hl,(BASIC_end_of_arrays)         ; adjust end of arrays
    sbc hl,bc
    ld (BASIC_end_of_arrays),hl

lece7h:
    call read_data_and_disks_off        ; load the program
    ld de,(BASIC_start_of_prog)         ; start of basic program in DE
    call BASIC_update_line_pointers     ; BASIC housekeeping
    ld hl,(BASIC_start_of_prog)         ; fill BASIC values
    dec hl
    ld (BASIC_start_minus_one),hl
    xor a
    ld (BASIC_on_error_active),a        ; set ON error inactive
    ld h,a
    ld l,a
    ld (BASIC_on_error_dest),hl         ; clear ON error destination
    ld (BASIC_byte_before_error),hl     ; clear error pointer
    call BASIC_CMD_RESTORE              ; RESTORE preps all DATA pointers
    ld bc,01d79h                        ; address in basic interpreter to continue from
    jp 01cceh                           ; (re)set other BASIC values and continue at address in BC
;
; detects string variables (also arrays) embedded in BASIC code
; moves their characters to string space
;
move_embedded_strings:
    ld hl,(BASIC_start_of_vars)         ; current start of variables
move_var_loop:
    ld de,(BASIC_start_of_arrays)       ; end of variables
    call BASIC_compare_HL_DE            ; all variables processed?
    jr z,move_arrays                    ; yes, do arrays as well
    push hl                             ; save pointer to current var
    ld a,(hl)                           ; get length
    cp 003h                             ; is it a string?
    jr nz,normal_var                    ; No

    push af                             ; save var type
    inc hl                              ; point to 1st byte of name
    bit 7,(hl)                          ; hi bit set?
    jr nz,led2ah                        ; ed23  20 05     .
    inc hl                              ; skip 1st
    inc hl                              ; and second char of name
    call move_to_stringspace
led2ah:
    pop af                              ; get type (len) back
normal_var:
    pop hl                              ; address of var
    ld c,003h                           ; always add 3 for the descriptor
    ld b,000h
    add a,c                             ; add size of variable
    ld c,a
    add hl,bc                           ; skip to next variable
    jr move_var_loop                    ; and loop

move_arrays:
    ld de,(BASIC_end_of_arrays)         ; have we reached the end of arrays?
    call BASIC_compare_HL_DE
    ret z                               ; yes! we're done...
    push hl                             ; save pointer to array descriptor
    ld a,(hl)                           ; get array type
    cp 003h                             ; is it a string array?
    call z,move_string_array            ; yes!

    pop hl                              ; array descriptor back
    inc hl                              ; skip type
    inc hl                              ; skip 2 name bytes
    inc hl
    ld e,(hl)                           ; length in DE
    inc hl
    ld d,(hl)
    add hl,de                           ; add to array descriptor pointer
    inc hl                              ; skip extra byte
    jr move_arrays                      ; and process next array

move_string_array:
    inc hl                              ; skip type
    inc hl                              ; skip 2 name bytes
    inc hl
    ld e,(hl)                           ; length in DE
    inc hl
    ld d,(hl)
    inc hl
    push hl                             ; save pointer to 1st dimension
    add hl,de                           ; end address of array elements
    ex de,hl                            ; put in DE
    pop hl                              ; pointer back
    ld b,(hl)                           ; # of dimensions in B
    inc hl
dimension_loop:
    inc hl                              ; skip 2 bytes of dimension size
    inc hl
    djnz dimension_loop                 ; for all dimensions

string_element_loop:
    push de                             ; save end address of array elements
    call move_to_stringspace            ; move from basic code to string space if necessary
    inc hl                              ; skip to next element
    inc hl
    inc hl
    pop de                              ; end address
    call BASIC_compare_HL_DE
    jr nz,string_element_loop           ; loop till all elements are done
    ret

move_to_stringspace:
    push hl                             ; save pointer to string info
    inc hl                              ; skip length
    ld e,(hl)                           ; get pointer to string in HL
    inc hl
    ld d,(hl)
    ex de,hl
    ld de,(BASIC_start_of_vars)         ; compare with end of basic
    call BASIC_compare_HL_DE            ; is it located in the basic program?
    pop hl                              ; pointer to string info back
    ret nc                              ; HL is outside the program!

    ld a,(hl)                           ; get length in A
    and a
    ret z                               ; return if string length is zero

    push hl                             ; save pointer to current string
    call BASIC_find_room_for_string     ; try to find room for the string. exits with error if no space
                                        ; returns with dest address for string in DE
    pop hl                              ; get string descriptor back

;
; copies string in descriptor to new location in stringspace
; HL : string descriptor
; DE : destination for string
; A  : non-zero length of string
;
copy_string:
    push hl                             ; and save
    inc hl                              ; skip length
    ld c,(hl)                           ; old address in HL via BC, new address in descriptor and DE
    ld (hl),e
    inc hl
    ld b,(hl)
    ld (hl),d
    ld h,b
    ld l,c
    ld b,000h                           ; prepare # of bytes to copy in BC
    ld c,a                              ; lo byte = length from descriptor
    ldir                                ; move string to stringspace
    pop hl                              ; pointer to descriptor back
    ret

dos_cmd_run:
    ld hl,(DOS_next_input_ptr)          ; pointer to input
    call BASIC_get_current_ch           ; advance to first chartacter of params
    call prepare_fileheader             ; read into fileheader
    call read_directory                 ; latest dir from drive
    call mon_file_to_dir_file           ; turn parsed filename into jwsdir file format
    call find_file_and_load
    jp BASIC_RUN

dos_cmd_wis:
    ld hl,(DOS_next_input_ptr)
    call BASIC_get_current_ch           ; advance to first chartacter of params
    ld a,(hl)                           ; is next char a '#"?
    cp '#'
    jr nz,erase_a_file                  ; no, erase a file
    call BASIC_get_next_ch              ; skip '#'
    ld a,(BASIC_Cassette_flags)         ; do we need confirmation?
    bit 7,a
    jr z,just_do_it                     ; no
    call disk_erase_directory           ; ask user!
    jr exit_cmd
just_do_it:
    call erase_directory_noask
exit_cmd:
    jp exit_save_load_cmd

erase_a_file:
    call prepare_fileheader
    call erase_file
    jr exit_cmd

dos_cmd_sys:
    call calc_expression                ; parse drive# from input in DE
    call set_sysdrive                   ; try to set system drive, exits with error if out of range
    jr exit_cmd
;
; return result of expression: (<expr>) in A and DE
;
calc_expression:
    ld hl,(DOS_next_input_ptr)
    call BASIC_CALC_EXPRESSION
    call next_char_valid
    db  ')'
    ret

txt_disk_wissen:
    db  chr_CRLF,chr_CYAN,"disk wissen",0

key_parser:
    push af                             ; save keycode
    cp JWS_CMD_numpad_M                 ;
    jr z,handle_cass_keys

    ld a,(dos_keymap_table)             ; # of mapped keys
    cp 9                                ; disk mode has 9 keys
    jr nz,handle_cass_keys              ; cassette mode

    pop af                              ; get keycode
    cp JWS_CMD_TAPE
    jr nz,handle_disk_keys
    ld a,5                              ; switch to cass mode (only 5 keys)
    ld (dos_keymap_table),a
    ld hl,txt_tape                      ; print 'Tape' and exit
    jr exit_key_parser

handle_disk_keys:
    cp JWS_CMD_DIR                      ; ZOEK
    jr z,parse_DOS_keys
    cp JWS_CMD_numpad_M                 ; in range of JWS-Commands?
    ret c                               ; too low: do nothing
    cp JWS_CMD_DISK+1
    ret nc                              ; too high: no action
    cp JWS_CMD_DISK                     ; if not the DISK key
    jr nz,parse_DOS_keys                ; then just parse it
    call StartDOS                       ; Activate JWS-DOS can happen only once
                                        ; because F000h onwards is overwritten by dir entries!
    jr parser_exit_to_basic

swap_to_disk_mode:
    ld a,9                              ; switch to disk mode (all 9 keys)
    ld (dos_keymap_table),a
    ld hl,txt_disk                      ; print 'Disk' and exit
exit_key_parser:
    call BASIC_Print_text
parser_exit_to_basic:
    jp return_to_basic

parse_DOS_keys:
    ld (savedstack),sp                  ; save BASIC stackpointer
    push af                             ; save keycode
    call fdc_drive_to_dos_drive         ; logical drive to physical drive
    pop af                              ; key code back
    jp dos_key_parser                   ; and process

handle_cass_keys:
    pop af                              ; get last char
    cp 088h                             ; JWS dos common disk/cassette commands are 088h and higher
    ret c                               ; not in range, do nothing
    ld hl,fkey_jump_table               ; pass jumptable for cassette commands
    ret

txt_tape:
    db              chr_CRLF, chr_YELLOW,"tape", 00h
txt_disk:
    db              chr_CRLF, chr_YELLOW,"disk", 00h
dos_keymap_table:
    db              9                   ; 9 keys are mapped from RAW keycode => JWS-command
; Disk- and Cassette mode have 5 keys in common
    db              RAW_KEY_DISK,       JWS_CMD_DISK
    db              RAW_KEY_M_shft9,    JWS_CMD_numpad_M
    db              RAW_KEY_INL,        JWS_CMD_LOAD
    db              RAW_KEY_OPN,        JWS_CMD_SAVE
    db              RAW_KEY_LIGHTNING,  JWS_CMD_COPY
; Disk mode has 4 extra keys
    db              RAW_KEY_shft5,      JWS_CMD_DEFRAG
    db              RAW_KEY_ZOEK,       JWS_CMD_DIR
    db              RAW_KEY_START,      JWS_CMD_RUN
    db              RAW_KEY_DEF,        JWS_CMD_AUTORUN

fkey_jump_table:
    dw              command_insert_done         ; 088h and 089h are not JWS commands, do nothing
    dw              command_insert_done         ;
    dw              insert_USR                  ; JWS_CMD_numpad_M
    dw              insert_EDIT                 ; JWS_CMD_COPY
    dw              insert_CSAVE                ; JWS_CMD_SAVE
    dw              insert_CLOAD                ; JWS_CMD_LOAD
    dw              swap_to_disk_mode           ; JWS_CMD_DISK

; key sequences for commands, as raw, unmapped keys
keys_usr_cmd:   db  085h, 026h, 00bh, 027h, 07eh, 02dh, 071h, 000h, 000h, 000h, 00ah    ; ?usr(0)
keys_cload_cmd: db  01ch, 041h, 031h, 022h, 00ch, 087h, 006h                            ; cload"
keys_csave_cmd: db  01ch, 00bh, 022h, 01fh, 024h, 087h, 006h                            ; csave"
keys_edit_cmd:  db  024h, 00ch, 046h, 025h, 004h                                        ; edit

insert_USR:
    ld hl,keys_usr_cmd                  ; start of ?Usr(0) command
    ld c,10                             ; length 10 keys
start_insert:
    ld de,06000h                        ; start of keyboard buffer
    ld b,000h                           ; prep for ldir
    ldir                                ; copy
    ld a,(hl)                           ; last byte = len
    ld (0600ch),a                       ; store in key-buffer count
command_insert_done:
    ret                                 ; done
insert_CLOAD:
    ld hl,keys_cload_cmd
command_len_6:
    ld c,6
    jr start_insert
insert_CSAVE:
    ld hl,keys_csave_cmd
    jr command_len_6
insert_EDIT:
    ld hl,keys_edit_cmd
    ld c,004h
    jr start_insert
;
; returns # of sectors for a disk-side+1 in HL
;
get_sectors_per_side:
    ld a,(system_drive)                 ; drive 0 active?
    and a
    jr nz,std_track_count               ; no, use standard # of tracks

    ld a,(ramdisk_status)               ; ramdisk active?
    and a
    jr z,std_track_count                ; no, use standard # of tracks
    ld a,(ramdisk_trackcount)           ; yes, load # of rmdisk tracks
    jr calc_sectors
std_track_count:
    ld a,(number_of_tracks)
; sectors = (tracks-1)*16 + 1
calc_sectors:
    ld b,a                              ; put tracks-1 in b
    dec b
    push de
    ld de,16                            ; times 16
    ld hl,00001h                        ; start with 1
calc_sector_loop:
    add hl,de                           ; add 16, b times
    djnz calc_sector_loop
    pop de
    ret

; convert from fdc drive code to actual drive #
; 0, 1, 2, 3 = side 1 drive 0,1,2,3
; 4, 5, 6, 7 = side 2 drive 0,1,2,3
; by removing bit 2:
; 4, 5, 6, 7 is also turned into 0, 1, 2, 3
fdc_drive_to_dos_drive:
    ld a,(system_drive)
    res 2,a                             ; 'and 3' does the same
    ld (system_drive),a
    ret
;
; is a basic program running?
; returns:
; Z : no running program
; NZ: program is runnning
; BASIC_active_program_line contains active line number when running basic
; contains 0ffffh if no line is active (basic is not running)
;
is_basic_running:
    push hl
    ld hl,(BASIC_active_program_line)
    inc hl                              ; inc will turn ffff into 0000
    ld a,h                              ; check if HL == 0
    or l
    pop hl
    ret                                 ; Z if 0, NZ if not 0
;
; returns:
;  Z = single sided
; NZ = double sided
;
is_disk_SS:
    ld a,(system_drive)                 ; drive 0 can be ramdisk
    and a
    jr nz,check_SS_or_DS
    ld a,(ramdisk_status)               ; ramdisk active?
    and a
    jr nz,signal_SS                     ; yes, ramdisk is also SS
check_SS_or_DS:
    ld a,(SS_DS_Char)                   ; get S or D
    cp 'S'                              ; S?
    ret nz                              ; no, signal DS
signal_SS:
    xor a                               ; set Zero flag
    ret

;
; returns:
; A : # of 256 byte sectors needed for file
;
sectors_for_file:
    ld a,(DE_filelen_LO)                ; # of bytes in last sector
    and a                               ; test for zero
    ld a,(DE_filelen_HI)                ; # of sectors
    ret z                               ; if multiple of 256 return full sectors
    inc a                               ; add sector for remaining bytes
    ret

disk_copy_file:
    ld hl,txt_copy                      ; prompt for filename
    call get_filename_and_header        ; and fill i ndetails
    ret c                               ; Stop pressed: exit!
    call ask_for_dest_drive             ; to which drive?
    cp STOP_key
    ret z                               ; Stop pressed: exit!
    ld sp,BASIC_top_of_temp_storage     ; stack to scratch area
    ld a,001h                           ; read last sector completely
    ld (DOS_only_full_sectors),a
    call start_fdc                      ; start drice(s)
    call get_directory
    call mon_file_to_dir_file           ; get file from system to working header info
    call copy_a_file
    jp exit_reset_sys_drive             ; restore source drive and exit to basic

txt_copy:
    db              chr_CRLF,'copy"',0

toggle_ramdisk:
    ld a,(ramdisk_trackcount)           ; # of ramdisk tracks
    and a                               ; if zero
    ret z                               ; do nothing
    ld a,(ramdisk_status)               ; toggle ramdisk status
    xor 1h
    ld (ramdisk_status),a
    and a                               ; if zero
    jr z,ramdisk_is_off                 ; print 'ram disk off' message
    ld hl,txt_ramdisk_aan               ; ram disk on message
    jr print_ramdisk_status
ramdisk_is_off:
    ld hl,txt_ramdisk_uit               ; RAM disk off message
print_ramdisk_status:
    jp BASIC_Print_text                 ; Print and exit

;
; returns:
; NZ = ramdisk inactive
;  Z = ramdisk active
;
is_ramdisk_active:
    ld a,(system_drive)                 ; active drive #
    and a                               ; drive 0?
    ret nz                              ; no, ramdisk cannot be active
    ld a,(ramdisk_status)               ; is ramdisk activated?
    cp 1                                ; NZ if inactive, Z if active
    ret
start_disks:
    call is_ramdisk_active
    jp nz,start_fdc
    ret
read_disk_IO_status:
    call is_ramdisk_active
    jp nz,MON_DSK_read_IO_status
    ret
is_write_enabled:
    call is_ramdisk_active
    jp nz,check_write_enable
    ret
stop_disks:
    call is_ramdisk_active
    jp nz,clear_FDC_and_interrupts
    ret
goto_track:
    call is_ramdisk_active
    jp nz,MON_DSK_gotrack
    ret
do_disk_IO:
    call is_ramdisk_active
    jp nz,perform_disk_IO
;
; do ramdisk IO
; E contains # of sectors to write
;
; REMARKS: 3 possible bugs in this code:
; - B is not initialized to 0 for 1st sector write.
; - track is not incremented after sector 16
; - loop does not have to check for ramdisk every sector
;
    ld hl,(dsk_transfer_adr)        ; start address of transfer
    ld a,(dsk_transfer_cmd_trk)         ; get 1 based track #
    dec a                               ; make 0 based
    out (ramdisk_Track),a               ; to ramdisk controller
    ld a,(dsk_transfer_cmd_sec)         ; sector to controller
    out (ramdisk_Sector),a
    inc a                               ; increment sector counter
    ld (dsk_transfer_cmd_sec),a         ; and store
    ld c,ramdisk_IO                     ; set correct port for otir/inir
    ld a,(dsk_transfer_cmd_IOtype)              ; read or write?
    res 7,a                             ; remove bit 7
    cp FDC_mode_write                   ; writing?
    jr z,write_ram_sector
    ld b,0                              ; assume read of full sector
    ld a,e                              ; E == 1 for last sector
    cp 1                                ; last sector?
    jr nz,read_ramdisk_bytes            ; no, write 256 bytes (b=0)
    ld a,(DOS_last_sector_flag)         ; is last sector 'full' or partial?
    bit 1,a                             ; bit 1 set means partial
    jr z,read_ramdisk_bytes             ; do full sector
    ld a,(DE_filelen_LO)                ; get # of significant bytes for last sector
    ld b,a
read_ramdisk_bytes:
    inir                                ; read!
next_ramdisk_sector:
    dec e                               ; decrement sectors to go
    ld (dsk_transfer_adr),hl            ; save address for next transfer
    jr nz,do_disk_IO                    ; repeat until done
    ret
write_ram_sector:
    otir
    jr next_ramdisk_sector
Intro_text:
    db      chr_CLS, chr_CYAN, chr_HEIGHT_DOUBLE
txt_JWS:
    db      "JWS DISK SYSTEM"
    db      chr_HEIGHT_NORMAL
    db      "(c)-1986"
    db      chr_GOTO_XY, 003h, 002h, chr_YELLOW
    db      "versie 5.0"
    db      chr_CYAN
    db      "NL"
    db      chr_GOTO_XY, 005h, 002h, chr_CYAN, 0

txt_DS_80Tr_drive:
    db          chr_CRLF, chr_GREEN
SS_DS_Char:
    db          "DS "
track_count_chars:
    db          "80Tr drive ",0
system_drive:
    db          001h
number_of_tracks:
    db          80+1                    ; default is 80 tracks (+1)

insert_dos_hook:
    ld a,chr_CLS                        ; clear screen character
    call BAS_CHAR_OUT                   ; and print
    call BAS_CURSOR_OFF                 ; hide cursor
    ld hl,dos_keymap_table              ; address of key-mappings for DOS
    ld (bas_key_map_table),hl           ; and place in the pointer of BASIC
    ld a,(P2000_cursor_type)            ; if 1 we are running on a P2000M
    and a                               ; Is it 1?
    jr z,not_P2000M                     ; No

    ; Handle attribute memory for the P2000M0
    ld hl,P2000_modelM_attributes       ; start of attribute meory to clear
    ld bc,007ffh                        ; 2048-1 bytes to clear
    xor a                               ; fill with 0
    ld (hl),a                           ; set first byte
    push hl                             ; transfer HL via stack
    pop de                              ; to DE
    inc de                              ; skip one; 1st byte already done
    ldir                                ; and fill the rest
    ld hl,BASIC_tab_spacing             ; Set tab size for P2000M
    ld (hl),DOS_P2000M_tab_size         ;
not_P2000M:
    ld de,BASIC_command_hook            ; Insert DOS command hook for BASIC ($60D0)
    ld hl,dos_hook                      ; start address for dos-hook
    call copy_3_bytes                       ; and set!

    ld hl,key_parser_hook               ; JP to DOS key parse code
    ld de,BASIC_keyparse_hook           ; where to copy the JP
    call copy_3_bytes                       ; do copy

    ld hl,(BASIC_string_save)           ; get string save JP address
    ld (string_save_Backup),hl          ; make backup
    ld hl,(BASIC_csave_vector)          ; get original csave address
    ld (csave_address_backup),hl        ; make backup

    ld hl,0dfffh                        ; do a "CLEAR 0xDFFF" to protect JWS-DOS code and data
    ld (BASIC_Top_of_Mem),hl            ; store as highest addres that BASIC may use
    ld de,00032h                        ; preserve the standard 50 bytes of string space
    sbc hl,de
    ld (BASIC_Start_Stringspace),hl     ; store as start of string space
    ld a,002h                           ; Set memory size to 36k
    ld (BASIC_Memory_Size),a            ; 1 = 16, 2 = 32, 3 = 48 kb

; remove ?Usr(0), used to activate JWS-DOS from usr-vector table
    ld b,00ah                           ; 10 entries in table
    ld de,00005h                        ; vector to find
    ld ix,BASIC_Usr_vectors             ; start of usr-vector table
usr_replace_loop:
    ld l,(ix+000h)                      ; get vector in HL
    ld h,(ix+001h)
    call BASIC_compare_HL_DE            ; Compare HL and DE: Z if HL==DE, S&C when HL<DE, NZ&P&NC when HL>DE
    jr z,replace_usr_vector             ; found, remove!
    inc ix                              ; next vector
    inc ix
    djnz usr_replace_loop               ; and loop max 10 times
    jr usr_replace_exit                 ; not found, exit
replace_usr_vector:
    ld hl,0289ch                        ; copy standard dummy usr vector
    ld (ix+000h),l                      ; over usr(0)
    ld (ix+001h),h

usr_replace_exit:                       ; validate checksum
    ld hl,ramdisk_tmp_storage+1         ; # of bytes to check in BC
    ld c,(hl)
    inc hl
    ld b,(hl)
    inc hl
    ld e,(hl)                           ; start value for checksum in de
    inc hl
    ld d,(hl)
    call checksum_control               ; decode/check will not return / reset when chcksum incorrect

    ld a,001h                           ; set basic active (1)                                          ; f089  3e 01   > .
    ld (dos_hook_active),a
    call init_ramdisk
    jp credits_and_go
;
; will check for the presence of a RAM-disk
; and dtermine the size:
;  64k = 16 tracks
; 256k = 64 tracks
; a track contains 16 sectors of 256 bytes
; # of tracks+1 (17 or 65) is stored in ramdisk_trackcount
; if no ramdisk is found, this location contains 0
;
init_ramdisk:
    ld a,001h                           ; save a byte to track 1, sector 1
    ld c,ramdisk_IO                     ; c contains IO address
    out (ramdisk_Track),a               ; set Track
    out (ramdisk_Sector),a              ; and Sector
    ld a,16+1                           ; write value 17. When we can read this back
                                        ; the ramdisk is at least 64k and we can save 17 as trackcount
    push af                             ; save value
    out (c),a                           ; write 17 to track1,sector 1

    out (ramdisk_Track),a               ; now set track to 17, and try to write there
    ld a,1                              ; set sector # to 1 this resets sector IO pointer.
                                        ; next IO will address first byte in the sector
    out (ramdisk_Sector),a
    in a,(c)                            ; read 1st byte of track 17, sector 1
    ld (ramdisk_tmp_storage),a          ; save value so we can restore it later
    pop af                              ; track 17
    push af                             ; and save again
    out (ramdisk_Track),a               ; set track
    ld a,001h                           ; and sector
    out (ramdisk_Sector),a
    ld a,64+1                           ; write value 65 to trk17,sec1
    out (c),a
    pop af                              ; track 17
    out (ramdisk_Track),a
    ld a,1                              ; Sector 1
    out (ramdisk_Sector),a
    in a,(c)                            ; read value from trk17,sec1
    cp 16+1                             ; compare with 17
    jr z,set_ramdisk_size               ; if equal, ramdisk is 64k
    cp 64+1                             ; is value 65?
    ret nz                              ; no, then no ramdisk is present!
;
; when ramdisk > 64k, it may contain user data
; the check overwrites 1 byte,
; restore the original value before continuing
;
    push af                             ; save # of tracks (65)
    ld a,1                              ; set ramdisk active
    ld (ramdisk_status),a
    out (ramdisk_Sector),a              ; set sector to 1
    ld a,16+1                           ; and track to 17
    out (ramdisk_Track),a
    ld a,(ramdisk_tmp_storage)          ; get original value
    out (c),a                           ; write to ramdisk
    pop af                              ; get trackcount

set_ramdisk_size:
    ld (ramdisk_trackcount),a           ; save # of tracks
    xor a                               ; set track,sector to 0,0
    out (ramdisk_Track),a
    out (ramdisk_Sector),a
    ld b,13                             ; length of data
    ld hl,ramdisk_tmp_data              ; destination address
    inir                                ; read 13 bytes
    call check_ramdisk_signature        ; signature found?
    ret z                               ; yes: RAMdisk already initialized, so we're done
;
; Initialize ramdisk = place signature and erase directory
;
; place signature on ramdisk
;
    xor a                               ; set track0, sector0
    out (ramdisk_Track),a
    out (ramdisk_Sector),a
    ld b,13                             ; 13 bytes to transfer
    ld hl,txt_JWStrikkers               ; data to write
    otir                                ; save to disk
;
; erase track 1, last 8 sectors (ramdisk directory)
;
    xor a                               ; create 8 sectors of 0's from f200-f9ff
    ld hl,0f200h
    push hl
    ld (hl),a
    ld de,0f201h
    ld bc,007ffh
    ldir
    pop hl                              ; pointer to f200 (8 sectors of 0's)

    ld bc,00897h                        ; 8 sectors (c) to port 97 (c)
    ld a,9                              ; start sector
lf10dh:
    push bc                             ; save count
    push af                             ; and A
    ld a,1                              ; set track 1
    out (ramdisk_Track),a               ;
    pop af                              ; get sector
    out (ramdisk_Sector),a              ; set
    ld b,0                              ; transfer 256 bytes
    otir
    inc a                               ; next sector
    pop bc                              ; get b for sectorcounter
    djnz lf10dh                         ; dec and loop
    ret                                 ; all done

check_ramdisk_signature:
    ld b,00dh                           ; 13 bytes to check
    ld hl,txt_JWStrikkers               ; wanted data
    ld de,ramdisk_tmp_data              ; data as read from ramdisk
lf127h:
    ld a,(de)                           ; get byte
    cp (hl)                             ; compare with wanted
    ret nz                              ; not equal: done, return NZ
    inc hl                              ; next byte
    inc de
    djnz lf127h                         ; and loop
    ret                                 ; found returns Z

    db              002h,04ah,001h
;
; calculate checksum.
; BC contains # of bytes to check
; DE contains startvalue for checksum
; method:
; BC bytes, starting at e000h, are added to DE
; if DE is 0 after this, all is ok and DOS can start
; otherwise reset!
;
checksum_control:
    ld hl,0e000h                        ; start of DOS code
    dec hl                              ; predecrement
checksum_loop:
    ld a,b                              ; all bytes done (bc == 0)?
    or c
    jr nz,checksum_next_byte

    ld a,d                              ; checksum OK (DE == 0)?
    or e
    ret z                               ; yes, all good!

    rst 0                               ; no: terminate with reboot

checksum_next_byte:
    inc hl                              ; point to next byte
    ld a,(hl)                           ; get it
    add a,e                             ; add to e
    jr nc,checksum_save_e               ; if carry
    inc d                               ; increment D
checksum_save_e:
    ld e,a                              ; and save E
    dec bc                              ; one more byte done
    jr checksum_loop                    ; and loop

key_parser_hook:
    jp key_parser

txt_JWStrikkers:
    db  "J.W.Strikkers"
;
; address where 13 bytes of signature, read from ramdisk are stored for comparison
;
ramdisk_tmp_data:
    db          03eh,004h,032h, 086h, 060h, 0c3h, 0a0h, 069h
