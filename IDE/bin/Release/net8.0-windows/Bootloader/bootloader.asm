[org 0x7C00]
[BITS 16]
start:
    mov dx, 0x3F8
    mov al, 'B'
    out dx, al

    xor ax, ax
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov sp, 0x7BFE

    mov al, 'S'
    out dx, al
    mov al, 'R'
    out dx, al

    mov ah, 0x00
    mov dl, 0x80
    int 0x13
    jc reset_fail
    mov al, 'Z'
    out dx, al
    jmp read_sector

reset_fail:
    mov al, 'X'
    out dx, al
    jmp hang

read_sector:
    mov ah, 0x02
    mov al, 8
    mov ch, 0
    mov cl, 2
    mov dh, 0
    mov dl, 0x80
    mov bx, 0x1000
    int 0x13
    jc read_fail
    mov al, 'A'
    out dx, al
    jmp success

read_fail:
    mov al, 'E'
    out dx, al
    jmp hang

success:
    mov al, 'L'
    out dx, al
    jmp mode_switch

mode_switch:
    mov ax, 0x3
    int 0x10

    cli
    lgdt [gdt_desc]
    mov eax, cr0
    or eax, 1
    mov cr0, eax
    jmp 0x08:protected_mode

hang:
    mov al, 'H'
    out dx, al
    jmp $

[BITS 32]
protected_mode:
    mov ax, 0x10
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov esp, 0x90000
    mov dx, 0x3F8
    mov al, 'K'
    out dx, al
    jmp 0x1000

gdt_start:
    dq 0
gdt_code:
    dw 0xFFFF
    dw 0
    db 0
    db 10011010b
    db 11001111b
    db 0
gdt_data:
    dw 0xFFFF
    dw 0
    db 0
    db 10010010b
    db 11111111b
    db 0
gdt_end:
gdt_desc:
    dw gdt_end - gdt_start - 1
    dd gdt_start

times 510 - ($ - $$) db 0
dw 0xAA55