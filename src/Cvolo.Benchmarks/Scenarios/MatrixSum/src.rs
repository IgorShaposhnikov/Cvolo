#[derive(Clone, Copy)]
struct Point {
    x: i32,
    y: i32,
}

fn run() -> i32 {
    let mut pts = vec![Point { x: 0, y: 0 }; 2048];
    let mut i = 0usize;
    while i < 2048 {
        pts[i].x = (i as i32) * 3;
        pts[i].y = (i as i32) + 1;
        i += 1;
    }
    let mut total = 0i32;
    let mut rep = 0i32;
    while rep < 2000 {
        i = 0;
        while i < 2048 {
            total += (pts[i].x & 255) + (pts[i].y & 255);
            i += 1;
        }
        rep += 1;
    }
    total
}

fn main() {
    println!("Answer: {}", run());
}