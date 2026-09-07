struct Point {
    x: i32,
    y: i32,
}

struct MyBox {
    v: i32,
}

#[inline(never)]
fn mix(p: &mut Point, b: &mut MyBox, n: i32) -> i32 {
    let mut i = 0;
    let mut acc = 0;
    while i < n {
        p.x = p.x + 1;
        b.v = b.v + 1;
        acc += (p.x & 255) + (b.v & 255);
        i += 1;
    }
    acc
}

fn main() {
    let mut p = Point { x: 1, y: 2 };
    let mut b = MyBox { v: 0 };
    println!("Answer: {}", mix(&mut p, &mut b, 20000000));
}
