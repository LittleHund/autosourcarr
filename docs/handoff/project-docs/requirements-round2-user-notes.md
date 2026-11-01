these are the changes or info i want
____________________________________
D1,		it is prefered to be standalone, so no suwayomi. komf is an extra like the arr companion apps.
D2,		every feature should be well integrated for optimal performance
D3,		requirement stays
D4,		since i'd like for it to becoma a docker image this is not a must, but it is prefered in case someone downloads the project and not the image. this should not impact performance, if it does don't apply the preference
D5,		compose should stay as minimal as possible, it is allowed to add variables overwritable by the compose. these should be well documented
D6,		requirement stays, but only if no pre-existing config is detected. autosourcarr.env seems to be some form of config, change name to config.json instead if the case.
D7,		requirement stays, this should include all manually configurated settings, unless it conflicts with new updates in which case you get notified about the changed setting due to update.
D8,		requirement stays if it is domething seperate from D9, else combine
D9,		requirement stays, yes
D10,	requirement stays, redesign sandbox if possible
D11,	requirement stays, also use postgres as an optional
D12,	this app should be converted/updated to C#/.net like sonarr/radarr instead of a lot of pythong scripts. C#/.net is my prefered coding language for creating apps
____________________________________
S1,		requirement stays, the repo is like an indexer for the sources, and the source is like an indexer for the manga and similar media
S2,		requirement stays, if possible add malware detection
S3,		requirement stays, the collapsible groups should be seperate headers from installed and uninstalled/not installed for ease of management
S4,		source settings should be editable from the discover and installed tab
S5,		requirement stays, make the webview as efficient as possible, last time i checked it felt clunky
S6,		requirement stays, auto-retry should be a good middle ground time
S7,		it should include a basic captcha solver and a way to connect other captcha solvers in the connections menu(like sonarr/radarr)
S8,		requirement stays
S9,		requirement stays
S10,	keep the raw source code of the latest confirmed working version
S11,	keep merged
S12,	requirement stays, should check if the original repo hasnt changed structures before repair in case the mihon app changed it's structure
S13,	requirement stays, this should include api points as well if available
S14,	requirement stays
S15,	requirement stays, sandbox requires permissions so i dont like it, use something that doesnt require permissions, but doesnt influence the app.
S16,	requirement stays as an opt-in. should be asked during setup and changable in settings
S17,	not a hard requirement, could live in a seperate activity tab
S18,	this should add a way to connect ai providers in connectors, custom included
S19,	should have a confirmed reliable way to sign into the ai providers, like device-code or opening a login webpage of some sort
S20,	requirement stays, there is no guarantee everyone runs multiple instances, or names them the same.
S21,	requirement stays, if ai is available add an opt-in for the benchmarking. when a benchmark is done, prevent/minimize ai overusage
____________________________________
M1,		requirement stays, should be a reliable matching logic. extra words but also less words lower score
M2,		requirement stays, this should be a way to prevent different entries with the same titles from matching
M3,		requirement stays, but should be a reliable process. title is not everything for this
M4,		requirement stays, make sure it keeps the whole titles and alt titles and not split it in places it shouldnt split
M5,		requirement stays, can be revised to make more reliable
M6,		requirement stays, this is to tweak sources for better merging. do some research to get better defaults for sources on keyoushi
M7,		requirement stays, but should be optimized
M8,		requirement stays, but should be reliable
M9,		requirement stays, the presets should be easy to configure, something like profiles in sonarr/radarr
M10,	requirement stays
M11,	requirement stays, large entries should be calculated through enabled sources. something like ((installed sources * 2) + 5). something reliable
M12,	requirement stays, a manually changed title should push the original listed title to alt titles if not already there
M13,	requirement stays
M14,	requirement stays
M15,	requirement stays, one copy can not be two different entries
M16,	requirement stays
M17,	requirement stays, sci-fi and science fiction are the same, so only list one. when one of the two(or more) is chosen use this for everything.
M18,	requirement stays, make more reliable
M19,	requirement should be opt-in setting, make this better and more reliable
M20,	requirement stays, machine translations should be as reliable as possible

M21 provisional,	maybe an opt-in setting that allows ai to search for chapters over the installed sources, only if reliable
____________________________________
I1,		do some research for a more optimal layout
I2,		requirement stays, should be optimized for more reliable matches
I3,		reuirement stays
I4,		rework this for a more optimal workflow
I5,		rework this for a more optimal workflow
I6,		this should be as optimal as possible without interfering with app performance
I7,		requirement stays, this should search for the entries without weird additions as a fallback, like (colored), (webtoon), (manga), etc.
I8,		requirement stays, optimize if possible
I9,		requirement stays, after catalogue reset, use the merge log to reference previous merges and recheck merge compatibility against (possibly) new rules
I10,	requirement stays, this should get the most likely data/match
____________________________________
X1,		requirement stays, should be as reliable as possible
X2,		requirement stays
X3,		requirement stays, summaries should be summaries. not weird data
X4,		requirement stays, build libraries and databases should be transferable/sharable to other local instances with completely seperate config/build/database, if version is not able to use/parse/import/implement the shared files, clearly note this in an error like: version mismatch, version X required
X5,		requirement stays, kinda the same as X4
X6,		requirement stays, each field prefers official data, but users can overwrite this with custom data or data from a selected source
____________________________________
C1,		requirement stays, make sure the chronology is correct, so i dont get chapter 1 part 2 before part 1 if there is no complete chapter 1
C2,		requirement stays, for season 2 chapter 1, keep counting from the last chapter of season 1, maybe something like "chapter 1: S1 ch.1: <additional chapter data/title>"
C3,		requirement stays, each source is still a seperate source, but for serving it picks the one with the most chapters as the priority is the same. groups dont need their own webview.
C4,		requirement stays, each chapter should stay it's own chapter to avoid confusion
C5,		requirement stays, optimize for more reliability
C6,		requirement stays
C7,		depends on if we make autosourcarr standalone and not require suwayomi
____________________________________
R1,		requirement stays, also add apk/jar download in the installed source tab for all installe sources
R2,		requirement stays, only if still applicable after the image/docker implementation
R3,		requirement stays, it should serve the entries available as fast as possible. update when source is loaded and the db is not up to date.
R4,		requirement stays, add an example of what authelia/npm config needs for this to work correctly
R5,		requirement stays, same for source downloads
R6,		requirement stays, implement suggestion
R7,		requirement stays, auto generate an admin token or some other token in a way that doesnt interfere with the app in another way.
____________________________________
U1,		requirement stays, unless improving navigation
U2,		requirement is changed as noted in notes/gap, navigation should be easy and optimal
U3,		requirement stays, do some research into themes listed over multiple kinds of webpages.
U4,		requirement stays, this is to improve navigation, overview and readability
U5,		requirement stays, it is annoying to click x every time
U6,		requirement stays, reopening the place you were is annoying to repeat every refresh
U7,		requirement stays, collapsible is optional but should be as easy to navigate as possible
U8,		requirement stays
U9,		requirement stays
U10,	requirement stays, chapters should be split into some columns for less scrolling when entries have a lot of chapters, for example if an entry has 300+ chapters it would save 200 chapters of scrolling
U11,	requirement stays
U12,	requirement stays, the app/webpage should not do weird things while in use
U13,	if possible implement this

U14 provisional,	live updating app. when a thumbnail or metadata or anything changes on whatever page, update it in the ui. so show the page in real-time
____________________________________
T1,		requirement stays, every task should be listed in one of the catagories. it should show ETA and time and date when it finished
T2,		recheck optimal way to handle this
T3,		requirement stays, it should load everything as quick as possible. if this is through caching the entries or something else doesnt matter. should add a cache limit in the settings, handle it as optimal as possible when cache limit is reached, like replacing entries with newest or something else.
T4,		recheck this and optimize
T5,		requirement stays, a button should be somewhere logical where i can download all the logs, errors, and othere relevant info. the file should obfuscate sensitive info like login data
T6,		requirement stays, group/combine the same errors to prevent cluttering of the ui
T7,		requirement stays, speed is important, especially with big catalogues
____________________________________
W1,		requirement stays, the wizard should go through all important and needed things and after that is done though all optional/opt-in things.
W2,		requirement stays
W3,		requirement stays, it should be easy to remove old deprecated app files. they should be auto-removed on image updates when docker is set up
W4,		requirement stays, only keep link settings of things that are required for the app to function properly
W5,		requirement stays, you should be able to configure database locations, have a logical default location
W6,		requirement stays
____________________________________
P1,		optimize for claude, prevent time-outs. should be as efficient and reliable as possible
P2,		requirement stays, check previous databases and research for relevant info that could improve quality
P3,		requirement stays, it should only save relevant info
P4,		requirement stays, execute tests at a logical point in the process and not for every little change
P5,		estimate ETA for the process and show remaining time at every update i get, should be an estimate of within 1 hour if not able to calculate exactly
P6,		requirement stays
P7,		requirement stays, the db is a log of everything that happened for autosourcarr usable in a quick way by the AI working on the project
____________________________________
A1,		implement proposal, first boot is allowed to take a little longer to preserve fast functionalit of the app. prefered boot time under 1 minute, is allowed to extend to 5 minutes. absolute boot ceiling is 10 minutes. do take some measurements
A2,		implement proposal
A3,		implement proposal
A4,		implement if still relevant
A5,		implement, old files/data can be removed as long as all relevant/useful data stays for better quality
A6,		implement proposal
____________________________________
13,		show me the decisions i need to make after all requirements are cleared up
1.		b, first create a plan and calculate an ETA. wait for my approval before execution
2.		b, make sure when viewing entries, there is a open in webview function so i can actually check chapters and things.
3.		answered in requirements
4.		answered in requirements
5.		accept for now, do some measurements to confirm optimal route