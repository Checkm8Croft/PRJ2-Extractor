# PRJ2 Extractor

PRJ2 Extractor is a tool for extracting from Tomb Raider levels, prj2 files that contains the geometry of the level.
The goal is making a tool for reconstructing a compiled Tomb Raider level, into an editable format.

The extracted level will not be perfect as the original one; reconstructing it 1:1 would be very challenging, so the level wil be accurated at 90-95%

Also this program is vibe-coded, so sorry for bad quality code

## TODO

- Add support for other Tomb Raider versions:
    - [ ] TR1
    - [ ] TR2
    - [ ] TR3
    - [x] TR4
    - [x] TRNG Levels
    - [x] TR5
    - [ ] TEN
- [x] Add PRJ2 extraction.
- [x] Write Geometry of rooms
- [x] Add level elements (sound sources, light points, sinks, etc.)
- [x] Assign Textures to rooms faces
- [ ] Add objects from the level file 
- [ ] Place Triggers

## Credits

- sapper-trle: for the base of the program, since I based my program from his one: [TR42PRJ](https://github.com/sapper-trle/TR42PRJ)
- TombEngine Team: for [TombLib](https://github.com/TombEngine/Tomb-Editor) used for writing PRJ2
- LostArtefacts Team: for [TombIO](https://github.com/LostArtefacts/TombIO) that helped me a little bit