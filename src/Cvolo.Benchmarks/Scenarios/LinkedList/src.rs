struct Node {
    next: Option<Box<Node>>,
    value: i32,
}

fn new_node(va: i32) -> Node {
    Node { next: None, value: va }
}

fn build(size: i32) -> Option<Box<Node>> {
    if size <= 0 {
        return None;
    }
    let mut head = Box::new(new_node(0));
    let mut i = 1i32;
    let mut curr = &mut head;
    while i < size {
        let nn = Box::new(new_node(i));
        curr.next = Some(nn);
        curr = curr.next.as_mut().unwrap();
        i += 1;
    }
    Some(head)
}

fn main() {
    let ring = build(10_000);
    let mut total = 0i32;
    if let Some(head) = ring {
        let mut walker: &Node = &head;
        let mut hops = 0i32;
        while hops < 10_000 {
            total += walker.value;
            match &walker.next {
                Some(nxt) => walker = nxt,
                None => hops = 20_000,
            }
            hops += 1;
        }
        let mut free = Some(head);
        while let Some(mut node) = free {
            free = node.next.take();
        }
    }
    println!("Answer: {}", total);
}
