# M1 acceptance test - does the shim reach a process your shell didn't launch?

The harness is already built and its wiring verified (the shim dispatches correctly by directory when run
with no env var set). The only remaining steps touch machine state, so **you** do them via the GUI - no
script from this session writes your PATH or creates the task, so nothing here trips CrowdStrike.

`node` here is a stand-in: the shim dispatches to little `.cmd` marker files, so you do **not** need Node
installed. A successful test prints a line starting `TACK-REACH-OK`.

---

## Step 1 - add ONE directory to the top of your USER Path (GUI, EDR-safe)

Add exactly this directory, at the **top** of the list:

```
C:\Users\JonHowell\Documents\git\personal\tack\spikes\m1-shim\reach-test\shims
```

How:
1. Press Start, type **"Edit environment variables for your account"**, open it. (This is the per-user
   dialog - do **not** use the system one.)
2. In the top list ("User variables"), select **Path**, click **Edit**.
3. Click **New**, paste the directory above, then use **Move Up** until it is **first** in the list.
   (First = it wins over any real `node` you may have.)
4. **OK** all the way out. Windows broadcasts the change; newly launched processes pick it up.

That's the whole change. (This is exactly what tack's installer will do in M5 - one user-PATH entry.)

---

## Step 2 - run the three tests

### Test A - Explorer (a window your shell didn't launch)
In File Explorer, go to `reach-test\` and **double-click `double-click-me.cmd`**.
A console opens (launched by Explorer, with Explorer's profile environment), runs a bare `node`, and should
print:
```
TACK-REACH-OK which=PROJB-BINDING cwd=...\reach-test\projB argv=--version
```

### Test B - Task Scheduler (a process the scheduler service launches with your profile env)
Import and run the prepared task (pick either option):

**Option 1 - command line** (run in a normal terminal you open yourself):
```
schtasks /Create /TN "tack-spike-reach" /XML "C:\Users\JonHowell\Documents\git\personal\tack\spikes\m1-shim\reach-test\tack-spike-reach.task.xml"
schtasks /Run    /TN "tack-spike-reach"
```
**Option 2 - GUI:** open **Task Scheduler** -> Action -> **Import Task...** -> pick
`reach-test\tack-spike-reach.task.xml` -> OK, then right-click the task -> **Run**.

Then open the proof file:
```
reach-test\proof\scheduler-proof.txt
```
It should contain `TACK-REACH-OK which=PROJB-BINDING ...`.

### Test C - Visual Studio (the case that matters)
1. **Start Visual Studio fresh** (close it first if it was already open - a running VS won't see the new
   PATH; it reads it once at launch, which is the entire point of this test).
2. Open `reach-test\VsReachTest\VsReachTest.csproj`.
3. **Build** the project (Build -> Build Solution).
4. Open the **Output** window (View -> Output, "Show output from: Build"). You should see:
   ```
   TACK-REACH-OK which=VS-PREBUILD cwd=...\reach-test\VsReachTest argv=--version
   ```
   That is Visual Studio's build resolving a bare `node` through the tack shim on PATH - proof that shims
   reach an IDE that never sourced your shell profile.

---

## Step 3 - undo (when done)

1. **Remove the PATH entry**: same dialog as Step 1, select the `...\reach-test\shims` entry, **Delete**, OK.
2. **Delete the task** (if you created it):
   ```
   schtasks /Delete /TN "tack-spike-reach" /F
   ```
   (or delete it in the Task Scheduler GUI).

Nothing else was changed on the machine.

---

## What each result tells you

| Test | Launcher | Proves |
| --- | --- | --- |
| A | Explorer | A GUI-launched process with profile env hits the shim via PATH. |
| B | Task Scheduler service | A process your shell never touched, env built from your profile, hits the shim - and picks the right per-directory binding. |
| C | Visual Studio (devenv) | The real IDE case from the plan: VS's build reaches the shim. |

All three rely on the same mechanism (shims dir first on the persisted user PATH), which is why proving any
of them proves the claim that distinguishes tack from shell-activation managers like `mise`/`nvm`.
