//! A `FakeElm` on the end of a TCP socket, the way a Wi-Fi dongle is (`FakeElmOverTcp`).
//!
//! Worth the socket rather than testing the transport against a stub. What is different
//! about this link is not the conversation, that is the same ASCII ELM327 exchange the other
//! radios carry, it is that the bytes arrive split across segments at the network's
//! convenience, that a closed connection reads as an endless supply of nothing, and that a
//! refused one is a different failure from a silent one. None of those exist unless there
//! is a real socket.
//!
//! One client at a time, as the adapters themselves are.

use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread::JoinHandle;
use std::time::Duration;

use olv_protocol::ms::ms_protocol::EcuTransport;

use super::FakeElm;

pub struct FakeElmOverTcp {
    port: u16,
    stopping: Arc<AtomicBool>,
    serving: Option<JoinHandle<()>>,
}

impl FakeElmOverTcp {
    pub fn new(car: &FakeElm) -> Self {
        Self::start(car, false)
    }

    /// Accepts a connection and drops it, as a dongle already in use does.
    pub fn hanging_up(car: &FakeElm) -> Self {
        Self::start(car, true)
    }

    fn start(car: &FakeElm, close_immediately: bool) -> Self {
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        listener.set_nonblocking(true).unwrap();
        let port = listener.local_addr().unwrap().port();

        let stopping = Arc::new(AtomicBool::new(false));
        let flag = Arc::clone(&stopping);
        let mut car = car.clone();

        let serving = std::thread::spawn(move || {
            let client = loop {
                match listener.accept() {
                    Ok((client, _)) => break client,
                    Err(_) if flag.load(Ordering::Relaxed) => return,
                    Err(_) => std::thread::sleep(Duration::from_millis(2)),
                }
            };

            if close_immediately {
                drop(client);
                return;
            }

            serve(client, &mut car, &flag);
        });

        Self {
            port,
            stopping,
            serving: Some(serving),
        }
    }

    /// The endpoint as the transport is asked for it.
    pub fn address(&self) -> String {
        format!("127.0.0.1:{}", self.port)
    }

    /// An address nothing is listening on, for the failure cases.
    pub fn closed_address() -> String {
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let port = listener.local_addr().unwrap().port();
        drop(listener);

        format!("127.0.0.1:{port}")
    }
}

fn serve(mut client: TcpStream, car: &mut FakeElm, stopping: &AtomicBool) {
    // The listener was non-blocking, and an accepted socket inherits that on some
    // platforms.
    client.set_nonblocking(false).unwrap();
    client.set_nodelay(true).unwrap();
    client.set_read_timeout(Some(Duration::from_millis(5))).unwrap();

    let mut arriving = [0u8; 512];
    let mut leaving = [0u8; 512];

    while !stopping.load(Ordering::Relaxed) {
        match client.read(&mut arriving) {
            Ok(0) => return,
            Ok(got) => {
                car.write(&arriving[..got]).unwrap();
            }
            Err(e) if matches!(e.kind(), std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut) => {}
            Err(_) => return,
        }

        let sending = car.read(&mut leaving, Duration::ZERO).unwrap();
        if sending == 0 {
            continue;
        }

        // In pieces, deliberately. A reply that always arrived whole would never exercise a
        // read that has to come back for the rest of it, which is the ordinary case on a
        // real link and the one that truncates a multi-module answer when it is got wrong.
        for piece in leaving[..sending].chunks(7) {
            if client.write_all(piece).is_err() {
                return;
            }
        }
    }
}

impl Drop for FakeElmOverTcp {
    fn drop(&mut self) {
        self.stopping.store(true, Ordering::Relaxed);

        // Waited on rather than abandoned: a server still holding the port while the next
        // test binds one is a failure in whichever test runs next.
        if let Some(serving) = self.serving.take() {
            let _ = serving.join();
        }
    }
}
