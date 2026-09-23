#include <stdbool.h>
#include <stdint.h>

typedef int32_t(*cvolo_callback)(int32_t);
typedef _Bool (*cvolo_bool_callback)(_Bool);

int32_t native_version = 7;
int32_t native_counter = 10;
cvolo_callback native_callback = 0;

int32_t call_callback(cvolo_callback cb, int32_t v) {
	return cb ? cb(v) : -1000;
}

void native_bump_version(void) {
	native_version += 1;
}

void native_bump_counter(void) {
	native_counter += 5;
}

int32_t call_stored_callback(int32_t v) {
	return native_callback ? native_callback(v) : -1000;
}

static int32_t native_add_three(int32_t v) {
	return v + 3;
}

cvolo_callback get_native_callback(void) {
	return native_add_three;
}

_Bool native_bool_identity(_Bool v) {
	return v;
}

_Bool call_bool_callback(cvolo_bool_callback cb, _Bool v) {
	return cb ? cb(v) : false;
}

struct small_pair {
	int32_t a;
	int32_t b;
};

struct large_triple {
	int64_t a;
	int64_t b;
	int64_t c;
};

struct small_pair native_small_pair(struct small_pair v) {
	v.a += 1;
	v.b += 2;
	return v;
}

struct large_triple native_large_triple(struct large_triple v) {
	v.a += 1;
	v.b += 2;
	v.c += 3;
	return v;
}

union native_number {
	int64_t i;
	double d;
};

union native_number native_union_add(union native_number v) {
	v.i += 5;
	return v;
}
