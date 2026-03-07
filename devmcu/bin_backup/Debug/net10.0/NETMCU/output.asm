; Method: Prog.M(), Offset: 0x0
    PUSH {r4-r11, lr}
    SUB SP, SP, #8
    MOV r4, r0
    @ Allocation: a -> Stack[0]
    MOV r6, r4
    MOV r0, r6
    BL Prog.GetNum()
    MOV r5, r0
    STR r5, [SP, #0]
Prog.M()_exit:
    ADD SP, SP, #8
    POP {r4-r11, pc}
    .align 4

; Method: Prog.GetNum(), Offset: 0x18
    PUSH {r4-r11, lr}
    MOV r4, r0
    MOV r0, #5
Prog.GetNum()_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.DelegateTest.MyMethod(), Offset: 0x1E
    PUSH {r4-r11, lr}
devmcu.DelegateTest.MyMethod()_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.DelegateTest.Test(), Offset: 0x22
    PUSH {r4-r11, lr}
    SUB SP, SP, #8
    @ Allocation: a -> Stack[0]
    MOV r0, #12
    BL NETMCU__Memory__Alloc
    @ Write TypeHeader for delegate System.Action
    LDR r5, =__type_literal_System_Action ; (placeholder for MOVW/MOVT)
    STR r5, [r0, #0]
    MOV r4, r0
    LDR r5, =devmcu.DelegateTest.MyMethod() ; (placeholder for address)
    STR r5, [r4, #8]
    MOV r6, #0
    STR r6, [r4, #4]
    STR r4, [SP, #0]
    LDR r4, [SP, #0]
    LDR r0, [r4, #4] @ Load Target
    LDR r5, [r4, #8] @ Load MethodPtr
    BLX r5 @ Invoke Delegate
    @ Allocation: b -> Stack[4]
    MOV r0, #12
    BL NETMCU__Memory__Alloc
    @ Write TypeHeader for delegate System.Action
    LDR r5, =__type_literal_System_Action ; (placeholder for MOVW/MOVT)
    STR r5, [r0, #0]
    MOV r4, r0
    LDR r5, =devmcu.DelegateTest.MyMethod() ; (placeholder for address)
    STR r5, [r4, #8]
    MOV r6, #0
    STR r6, [r4, #4]
    STR r4, [SP, #4]
    LDR r4, [SP, #4]
    LDR r0, [r4, #4] @ Load Target
    LDR r5, [r4, #8] @ Load MethodPtr
    BLX r5 @ Invoke Delegate
devmcu.DelegateTest.Test()_exit:
    ADD SP, SP, #8
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.BaseClass.GetValue(), Offset: 0x76
    PUSH {r4-r11, lr}
    MOV r4, r0
    MOV r0, #10
    B devmcu.BaseClass.GetValue()_exit
devmcu.BaseClass.GetValue()_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.BaseClass.Ping(int), Offset: 0x7E
    PUSH {r4-r11, lr}
    MOV r4, r0
    MOV r5, r1
    @ Save parameter 'val' from r1 to r5
    ADDS r0, r5, #1
    B devmcu.BaseClass.Ping(int)_exit
devmcu.BaseClass.Ping(int)_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.DerivedClass.GetValue(), Offset: 0x88
    PUSH {r4-r11, lr}
    MOV r4, r0
    MOV r0, #42
    B devmcu.DerivedClass.GetValue()_exit
devmcu.DerivedClass.GetValue()_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.ProcessArray(int[]), Offset: 0x90
    PUSH {r4-r11, lr}
    SUB SP, SP, #8
    MOV r4, r0
    @ Save parameter 'input' from r0 to r4
    @ Allocation: sum -> Stack[0]
    MOV r5, #0
    STR r5, [SP, #0]
    @ Allocation: i -> Stack[4]
    MOV r5, #0
    STR r5, [SP, #4]
L_FOR_START_0:
    LDR r5, [SP, #4]
    CMP r5, r6
    BGE L_FOR_END_1
    MOV r5, r4
    LDR r6, [SP, #4]
    LDR r7, [r5, #4] @ Load Array Length
    CMP r6, r7
    BCC L_BOUNDS_OK_3
    MOV r0, #0
    BL NETMCU_Throw
L_BOUNDS_OK_3:
    MOV r8, #1
    MULS r6, r8
    ADDS r6, r6, #8
    ADDS r5, r5, r6
    MOV r6, r4
    LDR r7, [SP, #4]
    LDR r8, [r6, #4] @ Load Array Length
    CMP r7, r8
    BCC L_BOUNDS_OK_4
    MOV r0, #0
    BL NETMCU_Throw
L_BOUNDS_OK_4:
    MOV r9, #1
    MULS r7, r9
    ADDS r7, r7, #8
    ADDS r6, r6, r7
    LDRB r0, [r6, #0]
    MOV r1, #2
    MULS r0, r1
    STRB r0, [r5, #0]
L_FOR_INC_2:
    B L_FOR_START_0
L_FOR_END_1:
    MOV r6, r4
    MOV r7, #0
    MOV r9, #4
    SUBS r10, r6, r9
    @ Read array length
    LDR r8, [r10, #0]
L_FOREACH_START_5:
    CMP r7, r8
    BGE L_FOREACH_END_6
    MOV r9, r7
    MOV r10, #4
    MULS r9, r10
    MOV r11, r6
    ADDS r11, r11, r9
    LDR r5, [r11, #0]
    MOV r0, r5
    LDR r9, [SP, #0]
    ADDS r9, r9, r0
    STR r9, [SP, #0]
L_FOREACH_INC_7:
    ADDS r7, r7, #1
    B L_FOREACH_START_5
L_FOREACH_END_6:
    LDR r0, [SP, #0]
    B devmcu.Program.ProcessArray(int[])_exit
devmcu.Program.ProcessArray(int[])_exit:
    ADD SP, SP, #8
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.RunTests(), Offset: 0x124
    PUSH {r4-r11, lr}
    BL devmcu.Program.TestDelegates()
    BL devmcu.Program.TestVirtualsAndInterfaces()
    BL devmcu.Program.TestArrays()
    BL devmcu.Program.TestBoxingAndCasting()
    BL devmcu.Program.TestStrings()
    BL devmcu.Program.TestRecords()
    BL devmcu.Program.TestGC()
    BL devmcu.Program.TestTuples()
    BL devmcu.Program.TestGenerics()
devmcu.Program.RunTests()_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.TestGenerics(), Offset: 0x14C
    PUSH {r4-r11, lr}
    SUB SP, SP, #24
    @ Allocation: intBox -> Stack[0]
    MOV r0, #8
    BL NETMCU__Memory__Alloc
    LDR r5, =__type_literal_NETMCU_Generated_Generics_Box_Int32 ; (placeholder for MOVW/MOVT)
    STR r5, [r0, #0]
    MOV r4, r0
    MOV r5, #123
    MOV r0, r4
    MOV r1, r5
    BL NETMCU_Generated_Generics.Box_Int32.Box_Int32(int)
    MOV r4, r0
    STR r4, [SP, #0]
    @ Allocation: v -> Stack[8]
    LDR r5, [SP, #0]
    MOV r0, r5
    BL NETMCU_Generated_Generics.Box_Int32.GetValue()
    MOV r4, r0
    STR r4, [SP, #8]
    @ Allocation: strBox -> Stack[12]
    MOV r0, #8
    BL NETMCU__Memory__Alloc
    LDR r5, =__type_literal_NETMCU_Generated_Generics_Box_String ; (placeholder for MOVW/MOVT)
    STR r5, [r0, #0]
    MOV r4, r0
    LDR r5, =__string_literal_15 ; (placeholder for MOVW/MOVT)
    MOV r0, r4
    MOV r1, r5
    BL NETMCU_Generated_Generics.Box_String.Box_String(string)
    MOV r4, r0
    STR r4, [SP, #12]
    @ Allocation: s -> Stack[20]
    LDR r5, [SP, #12]
    MOV r0, r5
    BL NETMCU_Generated_Generics.Box_String.GetValue()
    MOV r4, r0
    STR r4, [SP, #20]
devmcu.Program.TestGenerics()_exit:
    ADD SP, SP, #24
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.TestGC(), Offset: 0x1BE
    PUSH {r4-r11, lr}
    SUB SP, SP, #8
    BL System.GC.Collect()
    @ Allocation: mem -> Stack[0]
    MOV r5, #1
    MOV r0, r5
    BL System.GC.GetTotalMemory(bool)
    MOV r4, r0
    STR r4, [SP, #0]
devmcu.Program.TestGC()_exit:
    ADD SP, SP, #8
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.TestTuples(), Offset: 0x1DC
    PUSH {r4-r11, lr}
    SUB SP, SP, #16
    @ Allocation: t -> Stack[0]
    STR r4, [SP, #0]
    @ Allocation: a -> Stack[4]
    STR r4, [SP, #4]
    @ Allocation: b -> Stack[8]
    STR r4, [SP, #8]
    @ Allocation: c -> Stack[12]
    LDR r0, [SP, #4]
    STR r4, [SP, #12]
devmcu.Program.TestTuples()_exit:
    ADD SP, SP, #16
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.TestRecords(), Offset: 0x1FC
    PUSH {r4-r11, lr}
    SUB SP, SP, #16
    @ Allocation: p1 -> Stack[0]
    MOV r0, #12
    BL NETMCU__Memory__Alloc
    LDR r5, =__type_literal_devmcu_Program_Point ; (placeholder for MOVW/MOVT)
    STR r5, [r0, #0]
    MOV r4, r0
    MOV r5, #10
    MOV r6, #20
    MOV r0, r4
    MOV r1, r5
    MOV r2, r6
    BL devmcu.Program.Point.Point(int, int)
    MOV r4, r0
    STR r4, [SP, #0]
    @ Allocation: p2 -> Stack[4]
    MOV r0, #12
    BL NETMCU__Memory__Alloc
    LDR r5, =__type_literal_devmcu_Program_Point ; (placeholder for MOVW/MOVT)
    STR r5, [r0, #0]
    MOV r4, r0
    MOV r5, #10
    MOV r6, #20
    MOV r0, r4
    MOV r1, r5
    MOV r2, r6
    BL devmcu.Program.Point.Point(int, int)
    MOV r4, r0
    STR r4, [SP, #4]
    @ Allocation: p3 -> Stack[8]
    LDR r6, [SP, #0]
    MOV r0, r6
    BL devmcu.Program.Point._Clone()
    MOV r5, r0
    MOV r6, #30
    MOV r0, r5
    MOV r1, r6
    BL devmcu.Program.Point.WithY(int)
    MOV r4, r0
    STR r4, [SP, #8]
    @ Allocation: isEq -> Stack[12]
    LDR r5, [SP, #0]
    LDR r6, [SP, #4]
    CMP r5, r6
    BEQ L_BOOL_TRUE_0
    BNE L_BOOL_FALSE_1
L_BOOL_TRUE_0:
    MOV r4, #1
    B L_BOOL_END_2
L_BOOL_FALSE_1:
    MOV r4, #0
L_BOOL_END_2:
    STR r4, [SP, #12]
devmcu.Program.TestRecords()_exit:
    ADD SP, SP, #16
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.TestStrings(), Offset: 0x284
    PUSH {r4-r11, lr}
    SUB SP, SP, #32
    @ Allocation: hello -> Stack[0]
    LDR r4, =__string_literal_16 ; (placeholder for MOVW/MOVT)
    STR r4, [SP, #0]
    @ Allocation: length -> Stack[4]
    STR r4, [SP, #4]
    @ Allocation: h -> Stack[8]
    LDR r5, [SP, #0]
    MOV r6, #0
    LDR r7, [r5, #4] @ Load Array Length
    CMP r6, r7
    BCC L_BOUNDS_OK_0
    MOV r0, #0
    BL NETMCU_Throw
L_BOUNDS_OK_0:
    MOV r8, #4
    MULS r6, r8
    ADDS r6, r6, #8
    ADDS r5, r5, r6
    LDR r4, [r5, #0]
    STR r4, [SP, #8]
    @ Allocation: m -> Stack[12]
    LDR r5, [SP, #0]
    MOV r6, #7
    LDR r7, [r5, #4] @ Load Array Length
    CMP r6, r7
    BCC L_BOUNDS_OK_1
    MOV r0, #0
    BL NETMCU_Throw
L_BOUNDS_OK_1:
    MOV r8, #4
    MULS r6, r8
    ADDS r6, r6, #8
    ADDS r5, r5, r6
    LDR r4, [r5, #0]
    STR r4, [SP, #12]
    @ Allocation: isSame -> Stack[16]
    LDR r5, [SP, #0]
    LDR r6, =__string_literal_16 ; (placeholder for MOVW/MOVT)
    CMP r5, r6
    BEQ L_BOOL_TRUE_2
    BNE L_BOOL_FALSE_3
L_BOOL_TRUE_2:
    MOV r4, #1
    B L_BOOL_END_4
L_BOOL_FALSE_3:
    MOV r4, #0
L_BOOL_END_4:
    STR r4, [SP, #16]
    @ Allocation: isDiff -> Stack[20]
    LDR r5, [SP, #0]
    LDR r6, =__string_literal_17 ; (placeholder for MOVW/MOVT)
    CMP r5, r6
    BEQ L_BOOL_TRUE_5
    BNE L_BOOL_FALSE_6
L_BOOL_TRUE_5:
    MOV r4, #1
    B L_BOOL_END_7
L_BOOL_FALSE_6:
    MOV r4, #0
L_BOOL_END_7:
    STR r4, [SP, #20]
    @ Allocation: concat -> Stack[24]
    LDR r0, [SP, #0]
    ADDS r4, r0, #0
    STR r4, [SP, #24]
devmcu.Program.TestStrings()_exit:
    ADD SP, SP, #32
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.TestDelegates(), Offset: 0x322
    PUSH {r4-r11, lr}
    BL devmcu.DelegateTest.Test()
devmcu.Program.TestDelegates()_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.TestVirtualsAndInterfaces(), Offset: 0x32A
    PUSH {r4-r11, lr}
    SUB SP, SP, #24
    @ Allocation: b1 -> Stack[0]
    MOV r0, #4
    BL NETMCU__Memory__Alloc
    LDR r5, =__type_literal_devmcu_BaseClass ; (placeholder for MOVW/MOVT)
    STR r5, [r0, #0]
    MOV r4, r0
    STR r4, [SP, #0]
    @ Allocation: b2 -> Stack[4]
    MOV r0, #4
    BL NETMCU__Memory__Alloc
    LDR r5, =__type_literal_devmcu_DerivedClass ; (placeholder for MOVW/MOVT)
    STR r5, [r0, #0]
    MOV r4, r0
    STR r4, [SP, #4]
    @ Allocation: v1 -> Stack[8]
    LDR r5, [SP, #0]
    MOV r0, r5
    BL devmcu.BaseClass.GetValue()
    MOV r4, r0
    STR r4, [SP, #8]
    @ Allocation: v2 -> Stack[12]
    LDR r5, [SP, #4]
    MOV r0, r5
    BL devmcu.BaseClass.GetValue()
    MOV r4, r0
    STR r4, [SP, #12]
    @ Allocation: device -> Stack[16]
    LDR r4, [SP, #4]
    STR r4, [SP, #16]
    @ Allocation: interfaceR -> Stack[20]
    LDR r5, [SP, #16]
    LDR r0, [r5, #0] @ read TypeMetadata
    LDR r1, =__type_literal_devmcu_ITestDevice ; (placeholder for MOVW/MOVT)
    MOV r2, #0
    BL System.MCU.TypeHelper.FindInterfaceMethod(System.IntPtr, System.IntPtr, int)
    MOV r6, r0
    MOV r7, #100
    MOV r0, r5
    MOV r1, r7
    BLX r6
    MOV r4, r0
    STR r4, [SP, #20]
devmcu.Program.TestVirtualsAndInterfaces()_exit:
    ADD SP, SP, #24
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.TestArrays(), Offset: 0x3A8
    PUSH {r4-r11, lr}
    SUB SP, SP, #16
    @ Allocation: data -> Stack[0]
    MOV r5, #6
    MOV r6, #1
    MOV r6, r5
    MULS r6, r6
    ADDS r6, r6, #8
    MOV r0, r6
    BL NETMCU__Memory__Alloc
    @ Write TypeHeader for array int[]
    LDR r7, =__type_literal_int[] ; (placeholder for MOVW/MOVT)
    STR r7, [r0, #0]
    STR r5, [r0, #4] @ Array Length
    MOV r4, r0
    MOV r7, #10
    MOV r8, #0
    MOV r10, #1
    MULS r8, r10
    ADDS r8, r8, #8
    ADDS r9, r4, r8
    STRB r7, [r9, #0]
    MOV r7, #42
    MOV r8, #1
    MOV r10, #1
    MULS r8, r10
    ADDS r8, r8, #8
    ADDS r9, r4, r8
    STRB r7, [r9, #0]
    MOV r7, #3
    MOV r8, #2
    MOV r10, #1
    MULS r8, r10
    ADDS r8, r8, #8
    ADDS r9, r4, r8
    STRB r7, [r9, #0]
    MOV r7, #4
    MOV r8, #3
    MOV r10, #1
    MULS r8, r10
    ADDS r8, r8, #8
    ADDS r9, r4, r8
    STRB r7, [r9, #0]
    MOV r7, #5
    MOV r8, #4
    MOV r10, #1
    MULS r8, r10
    ADDS r8, r8, #8
    ADDS r9, r4, r8
    STRB r7, [r9, #0]
    MOV r7, #101
    MOV r8, #5
    MOV r10, #1
    MULS r8, r10
    ADDS r8, r8, #8
    ADDS r9, r4, r8
    STRB r7, [r9, #0]
    STR r4, [SP, #0]
    @ Allocation: result -> Stack[4]
    LDR r5, [SP, #0]
    MOV r0, r5
    BL devmcu.Program.ProcessArray(int[])
    MOV r4, r0
    STR r4, [SP, #4]
    @ Allocation: nested -> Stack[8]
    MOV r5, #2
    MOV r6, #4
    MOV r6, r5
    MULS r6, r6
    ADDS r6, r6, #8
    MOV r0, r6
    BL NETMCU__Memory__Alloc
    @ Write TypeHeader for array int[][]
    LDR r7, =__type_literal_int[][] ; (placeholder for MOVW/MOVT)
    STR r7, [r0, #0]
    STR r5, [r0, #4] @ Array Length
    MOV r4, r0
    STR r4, [SP, #8]
    LDR r4, [SP, #8]
    MOV r5, #0
    LDR r6, [r4, #4] @ Load Array Length
    CMP r5, r6
    BCC L_BOUNDS_OK_0
    MOV r0, #0
    BL NETMCU_Throw
L_BOUNDS_OK_0:
    MOV r7, #4
    MULS r5, r7
    ADDS r5, r5, #8
    ADDS r4, r4, r5
    MOV r5, #2
    MOV r6, #1
    MOV r6, r5
    MULS r6, r6
    ADDS r6, r6, #8
    MOV r0, r6
    BL NETMCU__Memory__Alloc
    @ Write TypeHeader for array int[]
    LDR r7, =__type_literal_int[] ; (placeholder for MOVW/MOVT)
    STR r7, [r0, #0]
    STR r5, [r0, #4] @ Array Length
    MOV r7, r0
    MOV r8, #1
    MOV r9, #0
    MOV r11, #1
    MULS r9, r11
    ADDS r9, r9, #8
    ADDS r10, r7, r9
    STRB r8, [r10, #0]
    MOV r8, #2
    MOV r9, #1
    MOV r11, #1
    MULS r9, r11
    ADDS r9, r9, #8
    ADDS r10, r7, r9
    STRB r8, [r10, #0]
    STR r0, [r4, #0]
    LDR r4, [SP, #8]
    MOV r5, #1
    LDR r6, [r4, #4] @ Load Array Length
    CMP r5, r6
    BCC L_BOUNDS_OK_1
    MOV r0, #0
    BL NETMCU_Throw
L_BOUNDS_OK_1:
    MOV r7, #4
    MULS r5, r7
    ADDS r5, r5, #8
    ADDS r4, r4, r5
    MOV r5, #3
    MOV r6, #1
    MOV r6, r5
    MULS r6, r6
    ADDS r6, r6, #8
    MOV r0, r6
    BL NETMCU__Memory__Alloc
    @ Write TypeHeader for array int[]
    LDR r7, =__type_literal_int[] ; (placeholder for MOVW/MOVT)
    STR r7, [r0, #0]
    STR r5, [r0, #4] @ Array Length
    MOV r7, r0
    MOV r8, #3
    MOV r9, #0
    MOV r11, #1
    MULS r9, r11
    ADDS r9, r9, #8
    ADDS r10, r7, r9
    STRB r8, [r10, #0]
    MOV r8, #4
    MOV r9, #1
    MOV r11, #1
    MULS r9, r11
    ADDS r9, r9, #8
    ADDS r10, r7, r9
    STRB r8, [r10, #0]
    MOV r8, #5
    MOV r9, #2
    MOV r11, #1
    MULS r9, r11
    ADDS r9, r9, #8
    ADDS r10, r7, r9
    STRB r8, [r10, #0]
    STR r0, [r4, #0]
    @ Allocation: n -> Stack[12]
    LDR r6, [SP, #8]
    MOV r7, #1
    LDR r8, [r6, #4] @ Load Array Length
    CMP r7, r8
    BCC L_BOUNDS_OK_2
    MOV r0, #0
    BL NETMCU_Throw
L_BOUNDS_OK_2:
    MOV r9, #4
    MULS r7, r9
    ADDS r7, r7, #8
    ADDS r6, r6, r7
    LDR r5, [r6, #0]
    MOV r6, #2
    LDR r7, [r5, #4] @ Load Array Length
    CMP r6, r7
    BCC L_BOUNDS_OK_3
    MOV r0, #0
    BL NETMCU_Throw
L_BOUNDS_OK_3:
    MOV r8, #1
    MULS r6, r8
    ADDS r6, r6, #8
    ADDS r5, r5, r6
    LDRB r4, [r5, #0]
    STR r4, [SP, #12]
devmcu.Program.TestArrays()_exit:
    ADD SP, SP, #16
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.TestBoxingAndCasting(), Offset: 0x528
    PUSH {r4-r11, lr}
    SUB SP, SP, #32
    @ Allocation: result -> Stack[0]
    MOV r4, #165
    STR r4, [SP, #0]
    @ Allocation: boxedResult -> Stack[4]
    LDR r5, [SP, #0]
    MOV r0, #8
    BL NETMCU__Memory__Alloc
    @ Write TypeHeader for Boxed int
    LDR r6, =__type_literal_int ; (placeholder for MOVW/MOVT)
    STR r6, [r0, #0]
    STR r5, [r0, #4]
    MOV r4, r0
    STR r4, [SP, #4]
    @ Allocation: unboxedResult -> Stack[8]
    LDR r4, [SP, #4]
    LDR r4, [r4, #4] @ CastUnbox Extract Payload
    STR r4, [SP, #8]
    @ Allocation: isInt -> Stack[12]
    LDR r5, [SP, #4]
    CMP r5, #0
    BEQ L_IS_FALSE_1
    LDR r6, [r5, #0] @ read TypeHeader
    LDR r7, =__type_literal_int ; (placeholder for MOVW/MOVT)
    CMP r6, r7
    BNE L_IS_FALSE_1
    MOV r4, #1
    B L_IS_END_0
L_IS_FALSE_1:
    MOV r4, #0
L_IS_END_0:
    STR r4, [SP, #12]
    @ Allocation: b2 -> Stack[16]
    MOV r0, #4
    BL NETMCU__Memory__Alloc
    LDR r5, =__type_literal_devmcu_DerivedClass ; (placeholder for MOVW/MOVT)
    STR r5, [r0, #0]
    MOV r4, r0
    STR r4, [SP, #16]
    @ Allocation: b3 -> Stack[20]
    LDR r5, [SP, #16]
    CMP r5, #0
    BEQ L_AS_FALSE_3
    LDR r6, [r5, #0] @ read TypeHeader
    LDR r7, =__type_literal_devmcu_DerivedClass ; (placeholder for MOVW/MOVT)
    CMP r6, r7
    BNE L_AS_FALSE_3
    MOV r4, r5
    B L_AS_END_2
L_AS_FALSE_3:
    MOV r4, #0
L_AS_END_2:
    STR r4, [SP, #20]
    @ Allocation: narrowed -> Stack[24]
    STR r4, [SP, #24]
devmcu.Program.TestBoxingAndCasting()_exit:
    ADD SP, SP, #32
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.Main(), Offset: 0x5AE
    PUSH {r4-r11, lr}
    SUB SP, SP, #8
    BL devmcu.Program.RunTests()
    BL NETMCUCore.STM.HAL.Init()
    MOV r4, #1
    MOV r0, r4
    BL NETMCUCore.STM.GPIO.EnableClock(NETMCUCore.STM.GPIO_Port)
    @ Allocation: usartConfig -> Stack[0]
    MOV r0, #20
    BL NETMCU__Memory__Alloc
    MOV r4, r0
    STR r4, [SP, #0]
    MOV r0, #192
    STR r0, [SP, #0]
    MOV r0, #2
    STR r0, [SP, #4]
    MOV r0, #0
    STR r0, [SP, #8]
    MOV r0, #3
    STR r0, [SP, #12]
    MOV r0, #7
    STR r0, [SP, #16]
    MOVW r4, #1073873920
    ADD r5, SP, #0
    MOV r0, r4
    MOV r1, r5
    BL HAL_GPIO_Init
    MOVW r4, #1073821696
    MOVW r5, #115200
    MOV r0, r4
    MOV r1, r5
    BL NETMCUCore.STM.USART.Init(NETMCUCore.STM.USART_Port, uint)
    MOVW r4, #1073821696
    LDR r5, =__string_literal_19 ; (placeholder for MOVW/MOVT)
    MOV r0, r4
    MOV r1, r5
    BL NETMCUCore.STM.USART.WriteLine(NETMCUCore.STM.USART_Port, string)
    LDR r4, =__string_literal_20 ; (placeholder for MOVW/MOVT)
    MOV r0, r4
    BL devmcu.Program.WriteLine(string)
    LDR r4, =__string_literal_21 ; (placeholder for MOVW/MOVT)
    MOV r0, r4
    BL devmcu.Program.WriteLine(string)
    @ Allocation: t -> Stack[4]
    MOV r4, #2
    STR r4, [SP, #4]
    LDR r4, [SP, #4]
    MOV r5, #2
    CMP r4, r5
    BEQ L_TRUE_0
    BNE L_FALSE_1
L_TRUE_0:
    MOV r4, #2
    MOV r5, #13
    MOV r6, #1
    MOV r0, r4
    MOV r1, r5
    MOV r2, r6
    BL NETMCUCore.STM.GPIO.SetMode(NETMCUCore.STM.GPIO_Port, int, NETMCUCore.STM.GPIO_Mode, NETMCUCore.STM.GPIO_Pull, NETMCUCore.STM.GPIO_Speed, uint)
    B L_END_2
L_FALSE_1:
L_END_2:
    MOV r4, #2
    MOV r0, r4
    BL NETMCUCore.STM.GPIO.EnableClock(NETMCUCore.STM.GPIO_Port)
    MOV r4, #2
    MOV r5, #13
    MOV r6, #1
    MOV r0, r4
    MOV r1, r5
    MOV r2, r6
    BL NETMCUCore.STM.GPIO.SetMode(NETMCUCore.STM.GPIO_Port, int, NETMCUCore.STM.GPIO_Mode, NETMCUCore.STM.GPIO_Pull, NETMCUCore.STM.GPIO_Speed, uint)
    MOV r4, #42
    MOV r0, r4
    BL devmcu.ABC.Process_Int32(int)
    LDR r4, =__string_literal_22 ; (placeholder for MOVW/MOVT)
    MOV r0, r4
    BL devmcu.ABC.Process_String(string)
    @ TRY BLOCK START
    ADR.W r0, L_CATCH_HANDLER_3
    BL NETMCU_TryPush
    BL NETMCU_TryPop
    B L_TRY_END_4
L_CATCH_HANDLER_3:
    BL NETMCU_TryPop
    @ CATCH BLOCK: System.Exception
    @ THROW EXECUTION
    mov r0, #0 @ Rethrow or null exception
    MOV r0, #0
    BL NETMCU_Throw
    B L_TRY_END_4
L_TRY_END_4:
    @ TRY BLOCK END
L_WHILE_START_5:
    MOV r4, #1
    CMP r4, #0
    BEQ L_WHILE_END_6
    MOV r4, #2
    MOV r5, #13
    MOV r0, r4
    MOV r1, r5
    BL NETMCUCore.STM.GPIO.Toggle(NETMCUCore.STM.GPIO_Port, int)
    MOVW r4, #500
    MOV r0, r4
    BL NETMCUCore.STM.HAL.Delay(int)
    B L_WHILE_START_5
L_WHILE_END_6:
devmcu.Program.Main()_exit:
    ADD SP, SP, #8
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.WriteLine(string), Offset: 0x6CE
    PUSH {r4-r11, lr}
    MOV r4, r0
    @ Save parameter 't' from r0 to r4
    B devmcu.Program.WriteLine(string)_exit
devmcu.Program.WriteLine(string)_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.Box<T>.GetValue(), Offset: 0x6D6
    PUSH {r4-r11, lr}
    MOV r4, r0
devmcu.Program.Box<T>.GetValue()_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.Box<T>.Box(T), Offset: 0x6DA

; Method: devmcu.Program.Point.WithX(int), Offset: 0x6DA
    PUSH {r4-r11, lr}
    MOV r4, r0
    MOV r5, r1
    @ Save parameter 'x' from r1 to r5
    MOV r0, r5
    MOV r1, r0
    BL devmcu.Program.Point.X.set
    B devmcu.Program.Point.WithX(int)_exit
devmcu.Program.Point.WithX(int)_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.Point.WithY(int), Offset: 0x6EA
    PUSH {r4-r11, lr}
    MOV r4, r0
    MOV r5, r1
    @ Save parameter 'y' from r1 to r5
    MOV r0, r5
    MOV r1, r0
    BL devmcu.Program.Point.Y.set
    B devmcu.Program.Point.WithY(int)_exit
devmcu.Program.Point.WithY(int)_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.Point._Clone(), Offset: 0x6FA
    PUSH {r4-r11, lr}
    MOV r4, r0
    MOV r0, #12
    BL NETMCU__Memory__Alloc
    LDR r5, =__type_literal_devmcu_Program_Point ; (placeholder for MOVW/MOVT)
    STR r5, [r0, #0]
    MOV r1, r5
    MOV r2, r6
    BL devmcu.Program.Point.Point(int, int)
    B devmcu.Program.Point._Clone()_exit
devmcu.Program.Point._Clone()_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.Program.Point.Point(int, int), Offset: 0x716

; Method: devmcu.Program.Point.X.get, Offset: 0x716

; Method: devmcu.Program.Point.X.set, Offset: 0x716

; Method: devmcu.Program.Point.Y.get, Offset: 0x716

; Method: devmcu.Program.Point.Y.set, Offset: 0x716

; Method: devmcu.ABC.TestMethod(), Offset: 0x716
    PUSH {r4-r11, lr}
devmcu.ABC.TestMethod()_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.ABC.Process<T>(T), Offset: 0x71A
    PUSH {r4-r11, lr}
    MOV r4, r0
    @ Save parameter 'value' from r0 to r4
    MOV r0, r4
    B devmcu.ABC.Process<T>(T)_exit
devmcu.ABC.Process<T>(T)_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.ABC.Process_Int32(int), Offset: 0x724
    PUSH {r4-r11, lr}
    MOV r4, r0
    @ Save parameter 'value' from r0 to r4
    MOV r0, r4
    B devmcu.ABC.Process_Int32(int)_exit
devmcu.ABC.Process_Int32(int)_exit:
    POP {r4-r11, pc}
    .align 4

; Method: devmcu.ABC.Process_String(string), Offset: 0x72E
    PUSH {r4-r11, lr}
    MOV r4, r0
    @ Save parameter 'value' from r0 to r4
    MOV r0, r4
    B devmcu.ABC.Process_String(string)_exit
devmcu.ABC.Process_String(string)_exit:
    POP {r4-r11, pc}
    .align 4

; Method: NETMCU_Generated_Generics.Box_Int32.GetValue(), Offset: 0x738
    PUSH {r4-r11, lr}
    MOV r4, r0
NETMCU_Generated_Generics.Box_Int32.GetValue()_exit:
    POP {r4-r11, pc}
    .align 4

; Method: NETMCU_Generated_Generics.Box_Int32.Box_Int32(int), Offset: 0x73C

; Method: NETMCU_Generated_Generics.Box_String.GetValue(), Offset: 0x73C
    PUSH {r4-r11, lr}
    MOV r4, r0
NETMCU_Generated_Generics.Box_String.GetValue()_exit:
    POP {r4-r11, pc}
    .align 4

; Method: NETMCU_Generated_Generics.Box_String.Box_String(string), Offset: 0x740

; Method: NETMCU_Generated_Generics.ValueTuple_Int32_Int32.ValueTuple_Int32_Int32(int, int), Offset: 0x740

