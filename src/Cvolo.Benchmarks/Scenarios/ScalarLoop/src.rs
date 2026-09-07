struct Point {
    x: i32,
    y: i32,
}

fn run(n: i32) -> i32 {
    let mut p = Point { x: 1, y: 2 };
    let mut i = 0i32;
    let mut acc = 0i32;
    while i < n {
        p.x += 3;
        p.y += 5;
        acc += (p.x & 255) + (p.y & 255);
        i += 1;
    }
    acc
}

fn main() {
    println!("Answer: {}", run(20_000_000));
}