# Race Management & Live Timekeeping Desktop App 🏁

<img width="1366" height="704" alt="image" src="https://github.com/user-attachments/assets/b420ad03-6045-4c5e-86dc-184fd89b2853" />

## 📖 Overview
Managing live sports events requires speed, precision, and reliable offline capabilities. This desktop application is a complete, standalone solution built to manage obstacle race timekeeping, heat generation, and real-time public leaderboards. 

Designed for both event operators and public audiences, it features a dual-screen architecture and a local network API to keep the event running smoothly without relying on an external internet connection.

## ✨ Key Features
* **Dual-Monitor Interface:** Independent screens for the administrative control panel (operator) and a clean, distraction-free live leaderboard (public display).
* **Participant & Dorsal Management:** Easily assign, track, and modify runner data across different heats.
* **Real-Time Data Persistence:** Built with SQLite for lightweight, reliable, and offline data storage.
* **Local Network Photo Uploads:** An embedded ASP.NET Core Minimal API allows event staff on the track to snap photos on their mobile devices and upload them directly to the desktop app over the local Wi-Fi.

## 🛠️ Tech Stack
* **Language:** C#
* **Framework:** WPF (Windows Presentation Foundation), .NET 
* **Database:** SQLite
* **Networking:** ASP.NET Core Minimal APIs

## 🧠 Technical Highlight: Embedded Local API
One of the core challenges was allowing mobile devices to interact with the desktop software in an environment without internet access. Instead of requiring a cloud backend, this application spins up a local HTTP server using Minimal APIs directly within the WPF lifecycle. This allows local track staff to hit a local IP address from their phones and push data instantly to the main operator screen.

## 🚀 Try It Out
You don't need to compile the code to see it in action.
1. Go to the [Releases]([https://github.com/abner2205/SistemaCarrera/releases/tag/v1.0.0]) page.
2. Download the latest `.rar` file.
3. Run the application on any Windows 10/11 machine.

## 💼 Available for Work
I am a Full-Stack and Desktop Application Developer specializing in solving complex business logic and creating robust software solutions. 

**Looking to build something similar? Let's talk:**
* [Hire me on Upwork]([https://www.upwork.com/freelancers/~010c818d163a020310?mp_source=share])
* [Hire me on Contra]([https://contra.com/armando_salas_3ho56idq?referralExperimentNid=DEFAULT_REFERRAL_PROGRAM&referrerUsername=armando_salas_3ho56idq])
* [Contact me via Email](mailto:[armandosalas22052003@gmail.com])
