#include <stdint.h>

/* --- Forward Declarations --- */
// Объявляем Reset_Handler до того, как засунуть его в таблицу векторов
void Reset_Handler(void); 

// Внешние символы из Linker Script (используем как массивы или указатели)
extern uint32_t _estack;
extern uint32_t _sdata;  // Начало секции данных в RAM
extern uint32_t _edata;  // Конец секции данных в RAM
extern uint32_t _sidata; // Начало секции данных во FLASH (LMA)
extern uint32_t _sbss;   // Начало BSS в RAM
extern uint32_t _ebss;   // Конец BSS в RAM

extern int main(void);

/* --- Vector Table --- */
__attribute__((section(".isr_vector")))
const uint32_t vectors[] = {
    (uint32_t)&_estack,    // 0: Начальное значение стека (SP)
    (uint32_t)Reset_Handler // 1: Точка входа в систему (PC)
};

/* --- Implementation --- */
void Reset_Handler(void) {
    // 1. Копируем секцию .data из FLASH в RAM
    uint32_t *pSrc = &_sidata;
    uint32_t *pDest = &_sdata;
    while (pDest < &_edata) {
        *pDest++ = *pSrc++;
    }

    // 2. Обнуляем секцию .bss (глобальные неинициализированные переменные)
    uint32_t *pBss = &_sbss;
    while (pBss < &_ebss) {
        *pBss++ = 0;
    }

    // 3. Теперь, когда память готова, вызываем main
    main();

    // Если main когда-нибудь вернет управление — зацикливаемся
    while(1);
}