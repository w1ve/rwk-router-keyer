import React, { useEffect, useState } from "react";
import ReactDOM from "react-dom/client";
import { invoke } from "@tauri-apps/api/core";
import App from "./App.jsx";
import StationApp from "./StationApp.jsx";
import "./styles.css";

// One web bundle serves both executables. The Station binary exposes a `station_app_role`
// command; the Client binary does not. We probe it once at startup: if it resolves we are the
// Station and mount StationApp, otherwise we are the Client and mount App. This avoids a
// build-time split and keeps a single Vite bundle for both apps.
function Root() {
  const [role, setRole] = useState(null); // null = probing, "client" | "station" otherwise

  useEffect(() => {
    let cancelled = false;
    invoke("station_app_role")
      .then((r) => {
        if (!cancelled) setRole(r === "station" ? "station" : "client");
      })
      .catch(() => {
        // The command is absent in the Client binary: that is how we know we are the Client.
        if (!cancelled) setRole("client");
      });
    return () => {
      cancelled = true;
    };
  }, []);

  if (role === null) {
    return (
      <div
        style={{
          height: "100%",
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          color: "var(--text-dim)",
          fontFamily: "Segoe UI, system-ui, sans-serif",
        }}
      >
        Starting…
      </div>
    );
  }

  return role === "station" ? <StationApp /> : <App />;
}

ReactDOM.createRoot(document.getElementById("root")).render(
  <React.StrictMode>
    <Root />
  </React.StrictMode>,
);
