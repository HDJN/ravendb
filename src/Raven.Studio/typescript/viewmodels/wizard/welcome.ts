import setupStep = require("viewmodels/wizard/setupStep");
import router = require("plugins/router");
import getSetupLocalNodeIpsCommand = require("commands/wizard/getSetupLocalNodeIpsCommand");
import getSetupParametersCommand = require("commands/wizard/getSetupParametersCommand");
import genUtils = require("common/generalUtils");

class welcome extends setupStep {
   
    activate(args: any) {
        super.activate(args, true);
        return $.when<any>(this.fetchLocalNodeIps(), this.fetchSetupParameters())
            .done((localIpsResult, setupParamsResult) => {
                this.model.init(setupParamsResult[0]);
                // todo: make the server endpoint return well defined classes    

                const ipV4 = _.filter(localIpsResult[0], (ip: string) => _.split(ip,  '.').length === 4);
                const ipV6 = _.difference(localIpsResult[0],  ipV4);
               
                this.model.localIps(_.uniq(_.concat(ipV4, ipV6)));
                
                // Remove localhost IPs if running on Docker
                if (setupParamsResult[0].IsDocker) {
                    this.model.localIps(_.filter(this.model.localIps(), (ip: string) => { return !genUtils.isLocalhostIpAddress(ip); }));
                }                
            });
    }

    private fetchLocalNodeIps() {
        return new getSetupLocalNodeIpsCommand()
            .execute();
    }
    
    private fetchSetupParameters() {
        return new getSetupParametersCommand() 
            .execute();            
    }
    
    chooseUnsecured() {
        this.model.mode("Unsecured");
        this.forwardToNextStep();
    }

    chooseSecured() {
        this.model.mode("Secured");
        this.forwardToNextStep();
    }

    chooseGenerate() {
        this.model.mode("LetsEncrypt");
        this.forwardToNextStep();
    }
    
    chooseContinue() {
        this.model.mode("Continue");
        router.navigate("#continue");
    }
    
    forwardToNextStep() {
        switch (this.model.mode()) {
            case "Unsecured":
                router.navigate("#unsecured");
                break;
            case "Secured":
                router.navigate("#certificate");
                break;
            case "LetsEncrypt":
                router.navigate("#license");
                break;
        }
    }
}

export = welcome;
