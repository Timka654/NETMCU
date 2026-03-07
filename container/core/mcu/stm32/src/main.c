#include <stdint.h>
#include <stdlib.h>
#include <sys/stat.h>
#include <errno.h>

// --- Runtime Exceptions ---
#define EXCEPTION_STACK_SIZE 64
static void* _ex_stack[EXCEPTION_STACK_SIZE];
static int _ex_stack_ptr = 0;

void NETMCU_TryPush(void* handler) {
    if (_ex_stack_ptr < EXCEPTION_STACK_SIZE) {
        _ex_stack[_ex_stack_ptr++] = handler;
    }
}

void NETMCU_TryPop() {
    if (_ex_stack_ptr > 0) {
        _ex_stack_ptr--;
    }
}

void NETMCU_Throw(void* exception_obj) {
    if (_ex_stack_ptr > 0) {
        // Берем верхний обработчик (catch) и вызываем его как функцию.
        // Он может выбросить further_throw, поэтому мы просто вызываем.
        void (*handler)(void*) = _ex_stack[_ex_stack_ptr - 1];
        handler(exception_obj);
    } else {
        // Unhandled exception hang
        while(1) { }
    }
}
// --------------------------

// Эти макросы придут из вашего C# через -D в MY_CFLAGS
#ifndef USER_CODE_ADDR
    #error "USER_CODE_ADDR must be defined!"
#endif

extern char _ebss; 
extern char _estack; // Start of the stack in RAM (highest address)

// --- Simple Mark-and-Sweep GC ---
typedef struct GcBlock {
    uint32_t size;              // Allocation size (payload only)
    struct GcBlock* next;       // Next block in the linked list
    uint32_t marked;            // 1 if marked, 0 if sweepable
} GcBlock;

static GcBlock* _gc_head = NULL;
static char* _heap_ptr = NULL;

// Prototypes
__attribute__((used)) void NETMCU__Memory__Collect();
__attribute__((used)) void NETMCU__Memory__Write(uint32_t addr, uint32_t val);
__attribute__((used)) uint32_t NETMCU__Memory__Read(uint32_t addr);
__attribute__((used)) void* NETMCU__Memory__Alloc(uint32_t size);
__attribute__((used)) void NETMCU__Memory__Free(void* ptr);

__attribute__((used)) void NETMCU__Memory__Write(uint32_t addr, uint32_t val) {
    *(volatile uint32_t*)addr = val;
}

__attribute__((used)) uint32_t NETMCU__Memory__Read(uint32_t addr) {
    return *(volatile uint32_t*)addr;
}

__attribute__((used)) void* NETMCU__Memory__Alloc(uint32_t size) {
    if (_heap_ptr == NULL) {
        // Initialize heap start
        // Ensure _ebss is 4-byte aligned
        _heap_ptr = (char*)((((uint32_t)&_ebss) + 3) & ~3);
    }

    // Allocate new block at the end of the heap
    // For simplicity, we just bump the pointer.
    // Real allocators would reuse freed blocks, but here we just append or reuse later via complex logic.
    // Let's implement a simple first-fit if we sweep, else bump.
    
    // First fit search in existing freed blocks (size == 0 is free? Or we have a free list? Let's just do bump allocation and a single list of active objects for now).
    // Actually, to make a true heap, let's just do a basic bump. This will leak memory until GC compacts, but we'll leave compaction for later.
    
    // Wait, if it's mark-and-sweep but we don't reuse, we will run out. 
    // We must reuse memory. Let's do a simple list:
    
    GcBlock* curr = _gc_head;
    GcBlock* best_fit = NULL;
    
    // Search for a block marked as free (marked = 0) with enough size
    while (curr != NULL) {
        if (curr->marked == 0 && curr->size >= size) {
            // we found a free block! For simplicity, don't split it, just reuse it.
            if (best_fit == NULL || curr->size < best_fit->size) {
                best_fit = curr;
            }
        }
        curr = curr->next;
    }
    
    if (best_fit != NULL) {
        best_fit->marked = 2; // Allocate as Black
        return (void*)((char*)best_fit + sizeof(GcBlock));
    }

    // Bump allocate
    uint32_t total_size = sizeof(GcBlock) + size;
    // Align to 4 bytes
    total_size = (total_size + 3) & ~3;

    // Check if we hit the stack (rough check: leave 1KB for stack)
    char* stack_ptr;
    __asm volatile ("mrs %0, msp" : "=r" (stack_ptr));
    if (_heap_ptr + total_size > stack_ptr - 1024) {
        // OOM! Trigger GC and try again
        NETMCU__Memory__Collect();

        // Try again after GC
        curr = _gc_head;
        while (curr != NULL) {
            if (curr->marked == 0 && curr->size >= size) {
                curr->marked = 2; // Allocate as Black
                return (void*)((char*)curr + sizeof(GcBlock));
            }
            curr = curr->next;
        }

        // Still OOM
        while(1) { } // Freeze
    }

    GcBlock* new_block = (GcBlock*)_heap_ptr;
    new_block->size = size;
    new_block->marked = 2; // Allocate as Black
    new_block->next = _gc_head;
    _gc_head = new_block;

    _heap_ptr += total_size;

    return (void*)((char*)new_block + sizeof(GcBlock));
}

__attribute__((used)) void NETMCU__Memory__Free(void* ptr) {
    if (ptr == NULL) return;
    GcBlock* block = (GcBlock*)((char*)ptr - sizeof(GcBlock));
    // We just mark it as free (sweepable), we don't unlink it to allow reuse
    block->marked = 0;
}

static void mark_pointers_in_range(uint32_t* start, uint32_t* end) {
    for (uint32_t* p = start; p < end; p++) {
        uint32_t val = *p;
        // Check if val points to our heap
        if (val >= (uint32_t)&_ebss && val < (uint32_t)_heap_ptr) {
            GcBlock* curr = _gc_head;
            while (curr != NULL) {
                uint32_t payload_addr = (uint32_t)((char*)curr + sizeof(GcBlock));
                if (val == payload_addr && curr->marked == 0) {
                    curr->marked = 1; // Mark as Gray (found, but children not scanned)
                    break;
                }
                curr = curr->next;
            }
        }
    }
}

// Полноценный консервативный сборщик мусора (Mark-and-Sweep с обходом графа)
__attribute__((used)) void NETMCU__Memory__Collect() {
    // 1. Unmark all (Reset to White)
    GcBlock* curr = _gc_head;
    while (curr != NULL) {
        curr->marked = 0;
        curr = curr->next;
    }

    // 2. Mark roots from Stack
    uint32_t* stack_ptr;
    __asm volatile ("mrs %0, msp" : "=r" (stack_ptr));
    uint32_t* stack_end = (uint32_t*)&_estack;

    if (stack_ptr < stack_end) {
        mark_pointers_in_range(stack_ptr, stack_end);
    }

    // 3. Mark roots from .data and .bss
    extern uint32_t _sdata, _edata, _sbss; 
    mark_pointers_in_range(&_sdata, &_edata);
    mark_pointers_in_range(&_sbss, (uint32_t*)&_ebss);

    // 4. Trace Iteratively (Gray to Black)
    int changed;
    do {
        changed = 0;
        curr = _gc_head;
        while (curr != NULL) {
            if (curr->marked == 1) { // If Gray
                curr->marked = 2;    // Mark Black (fully processed)
                uint32_t payload_addr = (uint32_t)((char*)curr + sizeof(GcBlock));
                // Scan interior pointers inside this object payload
                mark_pointers_in_range((uint32_t*)payload_addr, (uint32_t*)(payload_addr + curr->size));
                changed = 1;
            }
            curr = curr->next;
        }
    } while (changed != 0);

    // 5. Sweep implicitly:
    // Memory blocks with marked == 0 are now free for reuse.
    // Blocks with marked == 2 are active (Black).
}

// Поток / Время
__attribute__((used)) void NETMCU__Thread__Sleep(uint32_t ms) {
    // Здесь должна быть твоя реализация задержки (HAL_Delay или пустой цикл)
    for(volatile uint32_t i = 0; i < ms * 8000; i++); 
}

typedef void (*pFunction)(void);

volatile void* _keep_Memory_Write = (void*)NETMCU__Memory__Write;
volatile void* _keep_Memory_Read = (void*)NETMCU__Memory__Read;
volatile void* _keep_Memory_Alloc = (void*)NETMCU__Memory__Alloc;
volatile void* _keep_Memory_Free = (void*)NETMCU__Memory__Free;
volatile void* _keep_Memory_Collect = (void*)NETMCU__Memory__Collect;
volatile void* _keep_Thread_Sleep = (void*)NETMCU__Thread__Sleep;

// Функция для перехода к пользовательскому коду
void jump_to_user(uint32_t address) {
    uint32_t stack_pointer = *(volatile uint32_t*)address;
    uint32_t reset_handler = *(volatile uint32_t*)(address + 4);

    pFunction app_reset_handler = (pFunction)reset_handler;

    // Инициализация системных регистров (VTOR)
    // 0xE000ED08 - адрес регистра VTOR в Cortex-M4
    *(volatile uint32_t*)0xE000ED08 = address;

    // Установка Stack Pointer и переход
    __asm volatile ("msr msp, %0" : : "r" (stack_pointer));
    app_reset_handler();
}

int main(void) {
    // Здесь будет инициализация HAL, если вы её добавите через Include()
    
    // Прыжок по адресу, который прислал Roslyn
    jump_to_user(USER_CODE_ADDR);

    while(1);
}