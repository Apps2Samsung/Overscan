@ The smallest honest ARM shared object.
@
@ It exists to be loaded, not to be called: issue #17 needs to know whether this
@ app may map native code of its own at all on a retail set, and every measurement
@ so far has been made on a .NET assembly, which is a PE file and which Samsung's
@ loader hooks skip. One function so the file has a .text worth mapping, and no
@ libc reference so it links freestanding and depends on nothing the TV has to
@ supply.

    .arch armv7-a
    .text
    .global ov_probe_marker
    .type ov_probe_marker, %function
ov_probe_marker:
    mov r0, #1
    bx  lr
    .size ov_probe_marker, .-ov_probe_marker
