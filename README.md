# NexusFlow

> **A true peer-to-peer, failsafe-first multi-device input routing system for Windows.**

NexusFlow is a Windows desktop application that allows multiple computers on the same local network to cooperate as equals and seamlessly share **mouse and keyboard input** — with future support planned for audio and display sharing.

Unlike traditional client/server tools, NexusFlow is built on a **true peer-to-peer model** where every device is a *Peer*. There is no permanent host, no always-on server, and no single point of failure.

This project is being built with a strong focus on **safety**, **low latency**, and **clear user control**.

---

## ⚠️ Project Status

**Status: Phase 1 Complete — Phase 2 Active**

Phase 1 (stable input routing between two Windows peers) is complete. The core routing pipeline, failsafe, layout editor, auth, and transport are all production-ready for LAN use between Windows machines.

Phase 2 is now underway, focusing on reconnection resilience and screen sharing.

The project is intentionally being developed **in the open** to document architectural decisions, trade-offs, and lessons learned while building a real-time peer-to-peer system on Windows.

---

## 🎯 Problem Statement

Managing multiple computers simultaneously is common for developers, traders, content creators, and power users.

Existing solutions typically:

* rely on a **central server or host**
* have limited transparency or safety controls
* struggle with DPI scaling, mixed monitor layouts, or latency
* fail poorly when the network misbehaves

NexusFlow aims to solve this by:

* eliminating the server/client distinction
* making **failsafe behavior non-negotiable**
* aligning the UI with native Windows display concepts
* keeping all control local and explicit

---

## ✨ Core Principles

### 1. True Peer-to-Peer

* Every device runs the same application
* No permanent host or coordinator
* Temporary coordination only when required (e.g., conflict resolution)

### 2. Failsafe Always Wins

* **Shift + Esc** instantly blocks all remote input injection on the local device
* Works even if the network is stalled or disconnected
* Fully local — does not affect other peers
* A visible UI button performs the exact same action

### 3. Safety by Default

* No unauthenticated input injection
* Explicit trust required before any control is allowed
* HMAC-SHA256 authenticated transport after pairing

### 4. UI-First Desktop Experience

* Full Windows desktop application (not tray-first)
* Non-technical users should be able to understand and recover from any state

---

## 🧠 What NexusFlow Does (Phase 1)

### Implemented

* LAN-based peer discovery (UDP multicast)
* Explicit peer trust & pairing (ECDH + numeric compare-code)
* Authenticated peer-to-peer transport (TCP, binary framing)
* Seamless mouse & keyboard routing across peers
* Cursor edge detection with configurable layout — move your mouse off one screen and it appears on the next machine
* Proportional cursor warp — cursor enters the remote machine at the correct edge position relative to where it exited the local screen
* Global failsafe hotkey (Shift + Esc) with stuck-modifier release on disconnect
* Windows-style display layout editor with real monitor topology
* Persistent layout and trust decisions across restarts
* Detailed diagnostics and logging
* Zero-allocation hot path (binary wire protocol, pooled buffers, value-type events)

### Not Yet Implemented (By Design)

* Audio sharing
* Screen sharing / remote displays
* WAN / internet-based peers
* Android client
* macOS / Linux support

---

## 🧩 Input Routing Model (High-Level)

NexusFlow separates control into **two independent runtime concepts**:

### Active Target

* The peer currently receiving input
* Switches automatically when the cursor crosses a configured screen edge

### Active Input Source

* The peer whose physical devices are generating input
* Switches dynamically alongside the active target

### How Switching Works

* The cursor is tracked against the configured display layout
* When the cursor crosses the edge shared with a neighbouring peer, input is automatically routed to that peer
* On switch, the cursor is warped to the corresponding entry position on the remote screen — proportionally mapped so movement direction feels natural regardless of resolution difference
* A small hysteresis margin and 150ms cooldown prevent flapping at boundaries
* On disconnect or failsafe, all held modifier keys (Shift, Ctrl, Alt, Win) are released to prevent stuck keys

---

## 🖥 Display & Layout Model

* Each peer reports its local monitors using Windows APIs
* A peer is represented as a **single draggable block** in the layout editor
* Internal monitor layout is read-only (matches Windows Display Settings)
* Physical pixels are the source of truth
* Mixed resolutions, DPI scaling, and rotations are supported

Hot-plug behavior:

* Monitors are removed immediately on disconnect
* Routing edges are automatically stitched
* Reconnection restores the exact previous position

---

## 🏗 Architecture Overview

NexusFlow is built as a set of modular, testable components:

1. **Discovery** — UDP multicast (port 49721)
2. **Identity & Trust** — ECDH pairing, HMAC-SHA256, persisted trust store
3. **Transport** — TCP framing (port 49800), binary wire protocol
4. **Protocol** — Versioned, forward-compatible, zero-allocation codec
5. **Routing Engine** — Lamport-stamped Last-Write-Wins conflict resolution
6. **OS Integration** — Low-level Windows hooks, SendInput injection, cursor tracking
7. **UI Layer** — Avalonia MVVM, fully decoupled from Win32 layer

Key architectural rules:

* No uncontrolled global mutable state
* UI thread is never blocked by networking or hooks
* Every routing and protocol decision is loggable
* Input hot path produces zero heap allocations per event

---

## 🧰 Tech Stack (Phase 1)

* **Language:** C# (.NET 8)
* **UI:** Avalonia UI (MVVM)
* **Platform:** Windows 10 / 11 (x64)
* **OS Integration:** Windows APIs via P/Invoke (low-level hooks, SendInput, DPI, display topology)
* **Wire Protocol:** Custom binary framing — ~25 bytes per input event vs ~150 bytes JSON
* **Buffers:** `ArrayPool<byte>` throughout the network stack — no per-event heap allocation
* **DPI Awareness:** Per-Monitor DPI Aware v2

macOS and Linux are intentionally out of scope for Phase 1.

---

## 🗺 Roadmap

### Phase 1 ✅ Complete

* Stable input routing between two Windows peers
* Robust failsafe behavior
* Persisted layouts and trust decisions
* Full diagnostic visibility
* Zero-allocation hot path

### Phase 2 (In Progress)

* **Reconnection & Resilience** — automatic reconnection with exponential backoff, event retry queues, and visible connection state so brief network hiccups don't drop an active session
* **Screen Sharing Foundation** — Windows-side DXGI framebuffer capture, binary display stream protocol (`MessageType.Display`), and remote framebuffer rendering in the unified display space
* Android peer support (native Kotlin, QR-based pairing)

### Phase 3 (Exploratory)

* WAN support
* Audio routing
* Commercial-grade polish

---

## 🤝 Contributions & Feedback

This project is currently driven by a single developer, but feedback and architectural discussion are welcome.

If you're interested in:

* distributed systems
* real-time input routing
* Windows internals
* peer-to-peer architecture

…feel free to explore, raise issues, or start discussions.

---

## 📜 License

License to be decided.

---

> NexusFlow is not a finished product — it is a serious engineering project focused on correctness, safety, and long-term design.
