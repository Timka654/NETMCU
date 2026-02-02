# Пути внутри контейнера
BUILD_DIR = /project/build
LIBS_DIR = /project/libs

# Флаги компиляции для инклудов
CFLAGS += -I$(LIBS_DIR) -I/project/core/stm32/inc

# Флаги линкера для получения адресов функций
LDFLAGS += -T /project/core/stm32/linker_script.ld
LDFLAGS += -Wl,-Map=$(BUILD_DIR)/kernel.map,--cref