const fs=require("fs");
const t=fs.readFileSync(process.argv[2],"utf8").split("\n");
let n=0, mode="";
for (let i=0;i<t.length;i++){
  const l=t[i];
  if(l.startsWith("<<<<<<<")){n++;mode="ours";console.log("\n##### C"+n+" OURS:");continue;}
  if(l==="======="){mode="theirs";console.log("----- THEIRS:");continue;}
  if(l.startsWith(">>>>>>>")){mode="";continue;}
  if(mode) console.log(l);
}
