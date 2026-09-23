#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>

int main(void)
{
    printf("%zu\n", sizeof(_Bool));
    printf("%zu\n", sizeof(int8_t));
    printf("%zu\n", sizeof(uint8_t));
    printf("%zu\n", sizeof(int16_t));
    printf("%zu\n", sizeof(uint16_t));
    printf("%zu\n", sizeof(int32_t));
    printf("%zu\n", sizeof(uint32_t));
    printf("%zu\n", sizeof(int64_t));
    printf("%zu\n", sizeof(uint64_t));
    printf("%zu\n", sizeof(intptr_t));
    printf("%zu\n", sizeof(uintptr_t));
    printf("%zu\n", sizeof(uint8_t));
    printf("%zu\n", sizeof(float));
    printf("%zu\n", sizeof(double));
    return 0;
}
