var fs = require('fs');

var DCFile = [];

var classLookup = {};
var structLookup = {};
var fieldLookup = [];

var reverseFieldLookup = {};
var classFields = {};

var tempDC = [];


var index = 0;

var line = "";
var lindex = -1;


var typedefs = {};

var outside = false;

function searchDC(dc, name){
    var i = 0;
    while(i < dc[2].length){
        if(name == dc[2][i][1])
            return i;
        ++i;
    }
    return -1;
}

//reads up to delimeter
function readUpTo(del){
    if(!del) del = ' ';
    var temp = "";
    while(line[index] != del) temp += line[index++];
    index++; // skip del
    return temp;
}
function readUpToEither(dels){
    var temp = "";
    for(;;) {
        if(dels.indexOf(line[index]) > -1) break;
        temp += line[index++];
    }
    var del = line[index++];
    return [temp, del];
}

function readLine(){
    lindex++;
    index = 0;
    line = lines[lindex];
    
    if(line.length == 0){
        return;
    } else if(line[0] == '}'){
        outside = false;
        DCFile.push(tempDC);
        return;
    }
    
    if(!outside){
        var type = readUpTo(" ");
        switch(type){
            case 'from': // something pythony.. do I care?
                break;
            case 'typedef':
                var oldT = readUpTo(" ");
                var newT = readUpTo(";");
                
                if(newT[newT.length-1] == ']') {
                    // array clip
                    newT = newT.slice(0,-1);
                    newT = newT.split('[');
                    
                    oldT += '['+newT[1]+']';
                    
                    newT = newT[0];
                }
                
                typedefs[newT] = oldT;
                break;
            case 'struct':
                var structName = readUpTo(" ");
                outside = true;
                tempDC = ["struct", structName, []];
                structLookup[structName] = DCFile.length;
                break;
            case 'dclass':
                var className = readUpTo(" ");
                
                var inherited = [];
                
                if(line[index] == ':'){
                    // inheritance
                    index += 2;
                    
                    loop_cont: for(;;){
                        var tmp = readUpToEither([",", " "]);
                        var t_class = DCFile[classLookup[tmp[0]]];
                        if(!t_class){
                            console.log("NULL TClass "+(JSON.stringify(tmp)));
                            console.log(line);
                            continue loop_cont; // skip for now
                        }

                        var j = 0;
                        while(j < t_class[2].length){
                            inherited.push(t_class[2][j]);
                            reverseFieldLookup[className+"::"+t_class[2][j][1]] = reverseFieldLookup[tmp[0]+"::"+t_class[2][j][1]]
                            ++j;
                        }
                        index++;
                        if(tmp[1] == ' ' || line[index] == '{') break;
                    }
                }
                
                outside = true;
                tempDC = ["dclass", className, inherited];
                
                classLookup[className] = DCFile.length;
                break;
        }
    } else {
        index += 2; // two whitespace.. idk why
        
        tempDC[2].push(readType());
    }
}

function readType(){    
    var res = readUpToEither([" ", "("]);
    
    switch(res[1]){
        case ' ': // variable of some sort
            var type_v = res[0];
            var name_v = readUpToEither([" ", ";"]);
            
            
            if(name_v[0] == ':'){ // morph
                var name_m = res[0];
                var components = [];
                for(;;){
                    var temp = readUpToEither([",",";"]);
                    index += 1;
                    components.push(temp[0]);
                    if(temp[1] == ';') break;
                }
                var modifiers_m = [];
                var params_m = [];
                
                var i = 0;
                while(i < components.length){                    
                    var j = searchDC(tempDC, components[i++]);
                    if(j == -1){
                        console.log("ERROR: nonexistant component "+components[i-1]);
                    }
                    modifiers_m = tempDC[2][j][2];
                    params_m = params_m.concat(tempDC[2][j][3])
                }
                modifiers_m.push["morph"];
                reverseFieldLookup[tempDC[1]+"::"+name_m] = fieldLookup.length;
                fieldLookup.push([tempDC[1], "function", name_m, modifiers_m, params_m, components]);
                return ["function", name_m, modifiers_m, params_m, components];
                
                break;
            }
            
            var modifiers_v = [];
            if(name_v[1] == ' '){
                // modifiers
                for(;;){
                    var tmp_v = readUpToEither([" ", ";"]);
                    modifiers_v.push(tmp_v[0]);
                    if(tmp_v[1] == ';') break;
                }
            }
            name_v = name_v[0];
            
            // avoid clobbering array brackets with property name
            if(name_v[name_v.length-1] == ']'){
                name_v = name_v.slice(0, -2);
                type_v += "[]";
            }
            
            reverseFieldLookup[tempDC[1]+"::"+name_v] = fieldLookup.length;
            fieldLookup.push([tempDC[1], type_v, name_v, modifiers_v]);
            return [type_v, name_v, modifiers_v];
        case '(': // function
            var name_f = res[0];
            
            var params_f = [];
            
            
            for(;;){
                var param_f = readUpToEither([",","(", ")"]);
                while(param_f[0] == ' '){
                    param_f = param_f.slice(1);
                }
                if(param_f[1] == '('){
                    readUpTo(")");
                    
                    if(line[index+1] == '['){
                        index += 2;
                        var ind = readUpTo("]");
                        param_f[0] += " ["+ind+"]";
                    }
                    
                    params_f.push(param_f[0]);
                       
                    if(line[index++] == ')') break;
                } else {
                    params_f.push(param_f[0]);
                
                    if(param_f[1] == ')') break;
                    index++;
                }
                
            }
            
            var modifiers_f = [];
            if(line[index++] == ' '){
                // modifiers
                for(;;){
                    var tmp_f = readUpToEither([" ", ";"]);
                    modifiers_f.push(tmp_f[0]);
                    if(tmp_f[1] == ';') break;
                }
            }
            
            reverseFieldLookup[tempDC[1]+"::"+name_f] = fieldLookup.length;
            fieldLookup.push([tempDC[1], "function", name_f, modifiers_f, params_f]);
            return ["function", name_f, modifiers_f, params_f];
    }
}


module.exports = function(fname) {
    contents = fs.readFileSync(fname).toString();
    lines = contents.split('\n');

    var i = lines.length;
    while(i--){ readLine();}

    // dump
    (function(){
        //fs.writeFileSync("./DCFile.js", "module.exports.DCFile="+JSON.stringify(DCFile)+";module.exports.fieldLookup="+JSON.stringify(fieldLookup)+";module.exports.reverseFieldLookup="+JSON.stringify(reverseFieldLookup)+";module.exports.classLookup="+JSON.stringify(classLookup)+";module.exports.structLookup="+JSON.stringify(structLookup)+";module.exports.typedefs="+JSON.stringify(typedefs)+";");
    
		var csrootLevel = "public static string[] DCRoot = new string[] { ";
		var csreverseRootLevel = "public static Dictionary<string, UInt16> reverseDCRoot = new Dictionary<string, UInt16>{";
		
		var csfieldLookup = "public static string[][] fieldLookup = new string[][]{";
		var csfieldModifierLookup = "public static string[][] fieldModifierLookup = new string[][]{"
		var csfieldNameLookup = "public static string[] fieldNameLookup = new string[]{";
		var csreverseFieldLookup = "public static Dictionary<string, UInt16> reverseFieldLookup = new Dictionary<string, UInt16> {";
		
		var csclassLookup = "public static Dictionary<string, UInt16[]> classLookup = new Dictionary<string, UInt16[]> {";
		
		for(f = 0; f < DCFile.length; ++f) {
			csrootLevel += "\""+DCFile[f][1]+"\",";
			csreverseRootLevel += "{\""+DCFile[f][1]+"\", "+f+"},";
			
			var fieldVals = [];
			for(var n = 0; n < DCFile[f][2].length; ++n) {
				fieldVals.push(reverseFieldLookup[DCFile[f][1]+"::"+DCFile[f][2][n][1]]);
			}
			
			csclassLookup += "{\""+DCFile[f][1]+"\",new UInt16[]{"+(JSON.stringify(fieldVals).slice(1,-1))+"}},"
		}
		
		
		for(f = 0; f < fieldLookup.length; ++f) {
			var fieldArgs = fieldLookup[f][4];
			
			for(fs = 0; fs < fieldArgs.length; ++fs) {
				var type = fieldArgs[fs].split(" ").slice(0,-1).join("");
				if(typedefs[type]) type = typedefs[type];
				
				fieldArgs[fs] = type;
			}
			
			csfieldLookup += "new string [] {"+(JSON.stringify(fieldArgs).slice(1,-1))+"},";
			csfieldModifierLookup += "new string [] {"+(JSON.stringify(fieldLookup[f][3]).slice(1,-1))+"},";
			csfieldNameLookup += "\""+fieldLookup[f][2]+"\",";
		}
		
		var rfKeys = Object.keys(reverseFieldLookup);
		
		for(f = 0; f < rfKeys.length; ++f) {
			csreverseFieldLookup += "{\""+rfKeys[f]+"\", "+reverseFieldLookup[rfKeys[f]]+"},";
		}
		
		csrootLevel = csrootLevel.slice(0,-1) + "};\n";
		csreverseRootLevel = csreverseRootLevel.slice(0,-1) + "};\n";
		csfieldLookup = csfieldLookup.slice(0,-1) + "};\n";
		csfieldModiferLookup = csfieldModifierLookup.slice(0,-1) + "};\n";
		csfieldNameLookup = csfieldNameLookup.slice(0,-1) + "};\n";
		csreverseFieldLookup = csreverseFieldLookup.slice(0,-1) + "};\n";
		csclassLookup = csclassLookup.slice(0,-1) + "};\n";
		
		console.log("using System;\nusing System.Collections.Generic;\npublic static class DCFile {\n"+csrootLevel+csreverseRootLevel+csfieldLookup+csfieldModiferLookup+csfieldNameLookup+csreverseFieldLookup+csclassLookup+"};");
	})();
};

if(process.argv[2]) module.exports(process.argv[2])