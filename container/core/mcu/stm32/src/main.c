#include <stdint.h>
#include <stdlib.h>
#include <sys/stat.h>
#include <errno.h>

// Эти макросы придут из вашего C# через -D в MY_CFLAGS
#ifndef USER_CODE_ADDR
    #error "USER_CODE_ADDR must be defined!"
#endif

extern char _ebss; 

caddr_t _sbrk(int incr) {
    static char *heap_end;
    char *prev_heap_end;

    if (heap_end == 0) {
        heap_end = &_ebss;
    }

    prev_heap_end = heap_end;

    // Простая проверка: чтобы куча не залезла на стек
    // (Для начала можно оставить так, если памяти много)
    heap_end += incr;

    return (caddr_t) prev_heap_end;
}

// Память
void NETMCU__Memory__Write(uint32_t addr, uint32_t val) {
    *(volatile uint32_t*)addr = val;
}

uint32_t NETMCU__Memory__Read(uint32_t addr) {
    return *(volatile uint32_t*)addr;
}

// Куча (простейшая обертка над malloc или своим аллокатором)
void* NETMCU__Memory__Alloc(uint32_t size) {
    return malloc(size);
}

void NETMCU__Memory__Free(void* ptr) {
    free(ptr);
}

// Поток / Время
void NETMCU__Thread__Sleep(uint32_t ms) {
    // Здесь должна быть твоя реализация задержки (HAL_Delay или пустой цикл)
    for(volatile uint32_t i = 0; i < ms * 8000; i++); 
}

typedef void (*pFunction)(void);

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