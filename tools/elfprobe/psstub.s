@ A stand-in for libprivileged-service-client.so, the one library the engine's
@ implementation needs that Samsung's retail firmware will not let an app open
@ (issue #105: open(O_RDONLY) is EPERM, above Smack, on an AU7200 running Tizen
@ 6.0). The build-e1a648d census (docs/INTERNALS.md, *A set that passed the
@ third gate*) read the implementation's own import table and found that this is
@ all it takes from that library: four plain C functions, no data, nothing C++.
@
@ Loaded RTLD_GLOBAL by absolute path before ewk_init, so that when the loader
@ walks libchromium-impl.so's DT_NEEDED it finds this soname already in the
@ process and never goes to /usr/lib for the file it may not open. See
@ EngineStub.cs for the gate that decides when that happens — never on a set
@ where the engine starts on its own.
@
@ Each function says "done" and does nothing. PS_Mount / PS_Umount / PS_Mknod
@ are the privileged-service calls the engine would use to set up a sandbox
@ mount it does not need in an app that is only a browser; 0 is success in that
@ API's convention (and in every C convention), and an error here would be
@ reported by the engine as its own failure to start, which is what we have
@ already. PS_ErrorToString gets an empty string, so a caller that logs the
@ error of a call that never failed prints nothing rather than reading garbage.
@
@ No libc, no NEEDED of any kind, same as libovprobe.s: a dependency would make
@ a refusal to load this ambiguous, and the whole build is one question.

    .arch armv7-a
    .text

    .global PS_Mknod
    .type PS_Mknod, %function
PS_Mknod:
    mov r0, #0
    bx  lr
    .size PS_Mknod, .-PS_Mknod

    .global PS_Mount
    .type PS_Mount, %function
PS_Mount:
    mov r0, #0
    bx  lr
    .size PS_Mount, .-PS_Mount

    .global PS_Umount
    .type PS_Umount, %function
PS_Umount:
    mov r0, #0
    bx  lr
    .size PS_Umount, .-PS_Umount

    .global PS_ErrorToString
    .type PS_ErrorToString, %function
PS_ErrorToString:
    adr r0, ps_empty
    bx  lr
    .size PS_ErrorToString, .-PS_ErrorToString

@ In .text, PC-relative, on purpose: an `ldr r0, =label` into .rodata needs a text
@ relocation (TEXTREL), which the loader satisfies by making the code page
@ writable for a moment — and a firmware that refuses to map a file of ours
@ executable at all is not one to trust with writable-then-executable.
ps_empty:
    .byte 0
