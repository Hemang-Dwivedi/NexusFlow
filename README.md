# NexusFlow

> **A true peer-to-peer, failsafe-first multi-device input routing system for Windows.**

NexusFlow is a Windows desktop application that allows multiple computers on the same local network to cooperate as equals and seamlessly share **mouse and keyboard input** — with future support planned for audio and display sharing.

Unlike traditional client/server tools, NexusFlow is built on a **true peer-to-peer model** where every device is a *Peer*. There is no permanent host, no always-on server, and no single point of failure.

This project is being built with a strong focus on **safety**, **low latency**, and **clear user control**.

---

## ⚠️ Project Status

**Status: Active Development (Phase 1)**

NexusFlow is under heavy, ongoing development. Internal APIs, protocol formats, and implementation details may change frequently.

The project is intentionally being developed **in the open** to document architectural decisions, trade-offs, and lessons learned while building a real-time peer-to-peer system on Windows.
> Detailed internal architecture and customization documentation will be added once Phase 1 stabilizes.
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
* All connections are encrypted

### 4. UI-First Desktop Experience

* Full Windows desktop application (not tray-first)
* Non-technical users should be able to understand and recover from any state

---

## 🧠 What NexusFlow Does (Phase 1)

### Implemented / In Progress

* LAN-based peer discovery
* Explicit peer trust & pairing (numeric compare-code)
* Encrypted peer-to-peer transport
* Input routing foundations (mouse & keyboard)
* Global failsafe hotkey (Shift + Esc)
* Windows-style display layout editor
* Persistent settings across restarts
* Detailed diagnostics and logging

### Not Yet Implemented (By Design)

* Audio sharing
* Screen sharing / remote displays
* WAN / internet-based peers
* macOS / Linux support

---

## 🧩 Input Routing Model (High-Level)

NexusFlow separates control into **two independent runtime concepts**:

### Active Target

* The peer currently being controlled
* Determined by display layout and cursor movement

### Active Input Source

* The peer whose physical devices are generating input
* Switches dynamically based on user intent

### Switching Rules (Simplified)

* Keyboard press → immediate switch
* Mouse click / scroll → immediate switch
* Mouse movement → switch only after threshold
* Modifier keys are **stateful and preserved** across switches

This design prevents phantom input, stuck modifiers, and ambiguous ownership.

---

## 🖥 Display & Layout Model

* Each peer reports its local monitors using Windows APIs
* A peer is represented as a **single draggable block**
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

1. Discovery
2. Identity & Trust
3. Transport (Encrypted P2P)
4. Protocol (Versioned & forward-compatible)
5. Routing Engine
6. OS Integration (hooks, injection, hotkeys)
7. UI Layer (MVVM)

Key architectural rules:

* No uncontrolled global mutable state
* UI thread is never blocked by networking or hooks
* Every routing and protocol decision is loggable

---

## 🧰 Tech Stack (Phase 1)

* **Language:** C# (.NET 8)
* **UI:** Avalonia UI
* **Platform:** Windows 10 / 11 (x64)
* **OS Integration:** Windows APIs via P/Invoke
* **DPI Awareness:** Per-Monitor DPI Aware v2

macOS and Linux are intentionally out of scope for Phase 1.

---

## 🗺 Roadmap (High-Level)

### Phase 1 (Current)

* Stable input routing between two Windows peers
* Robust failsafe behavior
* Persisted layouts and trust decisions
* Full diagnostic visibility

### Phase 2 (Planned)

* Screen sharing (view-only)
* Remote display extension
* Android peer support (QR-based pairing)

### Phase 3 (Exploratory)

* WAN support
* Audio routing
* Commercial-grade polish

---

## 🤝 Contributions & Feedback

This project is currently driven by a single developer, but feedback and architectural discussion are welcome.

If you’re interested in:

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
