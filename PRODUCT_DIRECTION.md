# Product direction

John's Twitch Handler is a control center for the technical side of streaming. Its first question is not "what are the numbers?" but "is everything okay?"

John is the primary user and primary tester. Real streaming usefulness takes priority over presentation-only behavior. Demonstration features must remain isolated from OBS, connected accounts, real reports, and saved settings.

## The core experience

### Before the stream

Answer "am I ready to stream?" with automatic checks and one clear readiness result. Technical configuration remains available under advanced settings rather than becoming the main experience.

### During the stream

Answer "is my stream healthy?" by interpreting system, OBS, and connection signals. Detection uses deterministic and configurable rules. Raw metrics remain available as supporting evidence.

Corrective actions are classified as:

- Safe actions that are reversible and can run after a direct click.
- Guided actions that explain what John can do.
- Disruptive actions that require explicit confirmation and normally belong after the stream.

### After the stream

Answer "what happened?" with a readable summary, incident and recovery timeline, highlights, activity, and recommendations tied to observed evidence.

## Current development priority

Build the vertical Before, During, and After experience before adding more integrations. Demo Mode should simulate sustained GPU pressure or connection trouble, explain the deterministic diagnosis, demonstrate safe recovery, and carry the incident into an example post-stream summary.

World of Warcraft addon monitoring and Encounter Highlights are temporarily disabled. Their implementation remains available behind the `Features:WowTools` setting, but the cards perform no startup scan or background polling while disabled.

## Later packaging work

Automatic updates, a normal installer, and code signing remain future distribution work. They should not block owner testing or the early tester ZIP.
