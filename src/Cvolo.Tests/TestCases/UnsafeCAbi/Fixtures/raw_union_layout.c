#include <stddef.h>
#include <stdint.h>
#include <stdio.h>

union Value
{
    uint8_t B;
    int32_t I;
    double D;
};

struct Container
{
    uint8_t Prefix;
    union Value Payload;
};

int main(void)
{
    printf("%zu\n", sizeof(union Value));
    printf("%zu\n", _Alignof(union Value));
    printf("%zu\n", sizeof(union Value[2]));
    printf("%zu\n", offsetof(struct Container, Payload));
    printf("%zu\n", sizeof(struct Container));
    return 0;
}
