[BITS 32]

[org 0x1000]

section .text
global kernel_main

    jmp kernel_main

print_string:
    pusha
    mov esi, [esp+32]
    mov edi, 0xB8000
    mov ax,0x10
.print_loop:
    lodsb
    test al,al
    jz .done
    mov ah,0x0F
    stosw
    jmp .print_loop
.done:
    popa
    ret

print_fmt:
    push ebp
    mov ebp, esp
    push ebx
    push esi
    push edi
    mov esi, [ebp+8]
    mov edi, 0xB8000
    lea ebx, [ebp+12]
.loop:
    lodsb
    test al, al
    jz .done
    cmp al, 10
    je .loop
    cmp al, '%'
    jne .print
    lodsb
    cmp al, 'c'
    je .char
    cmp al, 'd'
    je .decimal
    cmp al, 'x'
    je .hex
    cmp al, 'f'
    je .float
    mov al, '?'
.print:
    mov ah, 0x0F
    stosw
    jmp .loop
.char:
    mov al, [ebx]
    mov ah, 0x0F
    stosw
    add ebx, 4
    jmp .loop
.decimal:
    mov eax, [ebx]
    call print_int
    add ebx, 4
    jmp .loop
.hex:
    mov eax, [ebx]
    call print_hex8
    add ebx, 4
    jmp .loop
.float:
    fld dword [ebx]
    call print_float
    add ebx, 4
    jmp .loop
.done:
    pop edi
    pop esi
    pop ebx
    pop ebp
    ret

print_int:
    push ebx
    push ecx
    push edx
    mov ebx, 10
    xor ecx, ecx
    test eax, eax
    jnz .div
    push eax
    inc ecx
    jmp .print
.div:
    xor edx, edx
    div ebx
    push edx
    inc ecx
    test eax, eax
    jnz .div
.print:
    pop eax
    add al, '0'
    mov ah, 0x0F
    stosw
    loop .print
    pop edx
    pop ecx
    pop ebx
    ret

print_hex8:
    push ebx
    push ecx
    push edx
    mov ecx, 8
.hloop:
    rol eax, 4
    mov ebx, eax
    and ebx, 0xF
    mov dl, [hex_chars + ebx]
    push eax
    mov al, dl
    mov ah, 0x0F
    stosw
    pop eax
    loop .hloop
    pop edx
    pop ecx
    pop ebx
    ret

print_float:
    push ebx
    push ecx
    push edx
    push esi
    sub esp, 12
    fst dword [esp]
    mov eax, [esp]
    test eax, 0x80000000
    jz .pos
    mov al, '-'
    mov ah, 0x0F
    stosw
    fchs
.pos:
    fld st0
    mov dword [esp+8], 0x3F000000
    fld dword [esp+8]
    fsubp st1, st0
    fistp dword [esp]
    mov eax, [esp]
    fild dword [esp]
    fsubp st1, st0
    mov eax, [esp]
    call print_int
    mov al, '.'
    mov ah, 0x0F
    stosw
    mov dword [esp+4], 1000000
    fild dword [esp+4]
    fmulp st1, st0
    fistp dword [esp]
    mov eax, [esp]
    test eax, eax
    jns .got_frac
    neg eax
.got_frac:
    mov esi, 100000
.frac_loop:
    xor edx, edx
    div esi
    add al, '0'
    mov ah, 0x0F
    stosw
    mov eax, edx
    mov edx, esi
    mov esi, 10
    push eax
    mov eax, edx
    xor edx, edx
    div esi
    mov esi, eax
    pop eax
    test esi, esi
    jnz .frac_loop
    add esp, 12
    pop esi
    pop edx
    pop ecx
    pop ebx
    ret

hex_chars db '0123456789ABCDEF'


; Function: kernel_main
kernel_main:
    push ebp
    mov ebp, esp
    sub esp, 512     ; Reserve stack space
    ; Call function: print_fmt
    mov eax, str0
    push eax         ; Push arg 0
    call print_fmt
    add esp, 4      ; Clean 1 args
.L0:  ; While loop start
    mov eax, 1
    test eax, eax
    jz .L1
    jmp .L0
.L1:  ; While loop end
.epilogue:
    mov esp, ebp
    pop ebp
    ret

section .data
str0 db `Hello World!`,0
vga_cursor dd 0

; Variables (initialized to zero)



; End of kernel
