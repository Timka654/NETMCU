#!/bin/bash

# 1. Останавливаем скрипт при любой ошибке
set -e

echo "--- Starting MCU Core Build ---"

# 2. Создаем папку для вывода, если её нет
mkdir -p /project/build

export MY_CFLAGS="-mcpu=%#MCU#% -mthumb -DUSER_CODE_ADDR=%#STARTUP_ADDRESS#% %#CFLAGS#% -nostartfiles -ffreestanding"


# 3. Компиляция ядра
# Используем переменные, которые придут из Docker ENV или будут подставлены твоим компилятором
echo "Step 1: Compiling ELF..."
# Ищем все C и ASM файлы в папках src и возможных дополнительных (native)
SRCS=$(find /project/src /project/native -name "*.c" -o -name "*.s" -o -name "*.cpp" 2>/dev/null)

arm-none-eabi-gcc $MY_CFLAGS \
	$SRCS \
	%#CFLAGS_LIBS#%
	-o /project/build/kernel.elf \
	-T /project/linker.ld \
	-Wl,-Map=/project/build/kernel.map,--cref

# 4. Конвертация в BIN (то, что раньше было в CMD)
echo "Step 2: Generating Binary..."
arm-none-eabi-objcopy -O binary /project/build/kernel.elf /project/build/kernel.bin

# 5. Опционально: вывод размера секций (очень полезно для STM32)
echo "--- Size Information ---"
arm-none-eabi-size /project/build/kernel.elf

echo "Build successful! Output: /project/build/kernel.bin"