#[derive(Clone, Copy)]
struct Body {
    x: f64, y: f64, z: f64,
    vx: f64, vy: f64, vz: f64,
}

fn run(n: i32) -> i32 {
    let mut bodies = [Body { x: 0.0, y: 0.0, z: 0.0, vx: 0.1, vy: 0.1, vz: 0.1 }; 100];
    let mut i = 0;
    while i < n {
        let mut j = 0;
        while j < 100 {
            bodies[j].x += bodies[j].vx;
            bodies[j].y += bodies[j].vy;
            bodies[j].z += bodies[j].vz;
            j += 1;
        }
        i += 1;
    }
    bodies[0].x as i32
}

fn main() {
    println!("Answer: {}", run(200_000));
}
