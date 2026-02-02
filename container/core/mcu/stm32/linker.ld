
MEMORY
{
  /* Указываем в чистых байтах (hex для наглядности) */
  FLASH (rx) : ORIGIN = 0x08000000, LENGTH = %#FLASH_SIZE#%
  RAM (xrw)  : ORIGIN = 0x20000000, LENGTH = %#RAM_SIZE#%
}

ENTRY(Reset_Handler)

SECTIONS
{
  .text :
  {
    KEEP(*(.isr_vector))
    *(.text*)
    *(.rodata*)
    . = ALIGN(4);
    _etext = .; 
  } > FLASH

  _sidata = LOADADDR(.data);

  .data : 
  {
    _sdata = .;
    *(.data*)
    . = ALIGN(4);
    _edata = .;
  } > RAM AT > FLASH

  .bss :
  {
    _sbss = .;
    *(.bss*)
    *(COMMON)
    . = ALIGN(4);
    _ebss = .;
	_end = .;
  } > RAM

  /* Символ для вершины стека */
  _estack = ORIGIN(RAM) + LENGTH(RAM);
}